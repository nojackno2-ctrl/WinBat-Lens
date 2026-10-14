//! 背景監測執行緒與它和 UI、托盤之間共享的狀態。
//!
//! 所有硬體、WMI 與效能計數器讀取都在這條執行緒上進行，UI 只會拿到已完成的
//! 快照。視窗顯示時每秒取樣一次，隱藏到托盤時降為每 5 秒一次。

use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use chrono::Local;

use crate::battery::PackInfo;
use crate::history::{PowerHistory, VoltageHistory};
use crate::models::RealTimePowerState;
use crate::realtime::Sampler;
use crate::tray::Tray;

/// 60 秒波形圖保留的取樣點數。
pub const MAX_CHART_POINTS: usize = 60;

const VISIBLE_INTERVAL: Duration = Duration::from_millis(1000);
const HIDDEN_INTERVAL: Duration = Duration::from_millis(5000);

/// One point of the waveform. Only values that were actually measured are
/// plotted; an unavailable reading contributes 0 rather than a fabricated
/// curve.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct ChartSample {
    pub discharge_w: f64,
    pub charge_w: f64,
    pub gpu_w: f64,
}

impl ChartSample {
    pub fn from_state(state: &RealTimePowerState) -> Self {
        Self {
            // The discharge flag also covers being plugged into a charger that
            // cannot keep up, so the shortfall lands on the discharge trace.
            discharge_w: if state.is_discharge_rate_measured { state.discharge_rate_w } else { 0.0 },
            charge_w: if state.is_charging && state.is_charge_rate_measured { state.charging_rate_w } else { 0.0 },
            gpu_w: if state.is_dgpu_power_measured { state.dgpu_power_w } else { 0.0 },
        }
    }

    pub fn peak(&self) -> f64 {
        self.discharge_w.max(self.charge_w).max(self.gpu_w)
    }
}

/// Everything the monitoring thread produces.
pub struct Live {
    pub state: Option<RealTimePowerState>,
    pub chart: VecDeque<ChartSample>,
    pub power_history: PowerHistory,
    pub voltage_history: VoltageHistory,
    /// The pack as the battery driver describes it right now, merged into
    /// reports generated on this machine.
    pub pack: PackInfo,
}

/// State shared between the UI thread, the monitoring thread and the tray.
pub struct Shared {
    live: Mutex<Live>,
    /// The main window is hidden in the notification area.
    pub hidden: AtomicBool,
    pub show_requested: AtomicBool,
    pub check_requested: AtomicBool,
    pub exit_requested: AtomicBool,
    pub auto_start_changed: AtomicBool,
}

impl Shared {
    pub fn new(voltage_history: VoltageHistory, start_hidden: bool) -> Self {
        Self {
            live: Mutex::new(Live {
                state: None,
                chart: VecDeque::with_capacity(MAX_CHART_POINTS + 1),
                power_history: PowerHistory::default(),
                voltage_history,
                pack: PackInfo::default(),
            }),
            hidden: AtomicBool::new(start_hidden),
            show_requested: AtomicBool::new(false),
            check_requested: AtomicBool::new(false),
            exit_requested: AtomicBool::new(false),
            auto_start_changed: AtomicBool::new(false),
        }
    }

    /// A panic while holding the lock must not take the whole app down with a
    /// poisoned mutex; the data is plain state and still usable.
    pub fn live(&self) -> MutexGuard<'_, Live> {
        self.live.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// Feeds one sample in exactly as the monitoring thread would.
    #[cfg(test)]
    pub fn record_for_test(&self, state: &RealTimePowerState, pack: PackInfo) {
        self.record(state, pack);
    }

    fn record(&self, state: &RealTimePowerState, pack: PackInfo) {
        let mut live = self.live();

        live.power_history.add_from_power_state(state);

        // Voltage history is event-based: a second-by-second poll at the same
        // battery percentage is intentionally ignored.
        if state.is_voltage_measured {
            live.voltage_history.record_on_percent_change(
                state.battery_percent,
                state.battery_voltage_v,
                Local::now().naive_local(),
            );
        }

        live.chart.push_back(ChartSample::from_state(state));
        while live.chart.len() > MAX_CHART_POINTS {
            live.chart.pop_front();
        }

        live.state = Some(state.clone());
        live.pack = pack;
    }
}

/// Starts the monitoring loop. `wake_ui` is called after each sample taken
/// while the window is visible.
pub fn spawn(shared: Arc<Shared>, tray: Option<Arc<Tray>>, wake_ui: impl Fn() + Send + 'static) {
    let spawned = std::thread::Builder::new().name("monitor".into()).spawn(move || {
        // Opening the sensor stack (NVML, WMI, PDH) can take a moment, which
        // is exactly why it happens here and not on the UI thread.
        let mut sampler = Sampler::new();

        #[cfg(debug_assertions)]
        let demo = std::env::var("WINBAT_LENS_DEMO").ok();
        #[cfg(debug_assertions)]
        let mut tick = 0u64;

        while !shared.exit_requested.load(Ordering::Relaxed) {
            let state = sampler.sample();
            let pack = sampler.pack_info();

            #[cfg(debug_assertions)]
            let (state, pack) = match &demo {
                Some(scenario) => {
                    tick += 1;
                    (crate::realtime::demo::state(scenario, tick, &state), crate::realtime::demo::pack())
                }
                None => (state, pack),
            };
            shared.record(&state, pack);

            if let Some(tray) = &tray {
                tray.update(&state);
            }

            let hidden = shared.hidden.load(Ordering::Relaxed);
            if !hidden {
                wake_ui();
            }

            // Sleep in short slices so a restore from the tray gets a fresh
            // sample promptly instead of waiting out the 5 s background tick.
            let interval = if hidden { HIDDEN_INTERVAL } else { VISIBLE_INTERVAL };
            let started = Instant::now();
            while started.elapsed() < interval {
                std::thread::sleep(Duration::from_millis(100));
                if shared.exit_requested.load(Ordering::Relaxed) || (hidden && !shared.hidden.load(Ordering::Relaxed)) {
                    break;
                }
            }
        }
    });
    let _ = spawned;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn chart_plots_only_measured_values() {
        let unmeasured = RealTimePowerState {
            discharge_rate_w: 12.0,
            charging_rate_w: 30.0,
            dgpu_power_w: 9.0,
            is_charging: true,
            ..Default::default()
        };
        assert_eq!(ChartSample::from_state(&unmeasured), ChartSample::default());

        let measured = RealTimePowerState {
            discharge_rate_w: 12.0,
            is_discharge_rate_measured: true,
            dgpu_power_w: 19.0,
            is_dgpu_power_measured: true,
            ..Default::default()
        };
        let sample = ChartSample::from_state(&measured);
        assert_eq!(sample, ChartSample { discharge_w: 12.0, charge_w: 0.0, gpu_w: 19.0 });
        assert_eq!(sample.peak(), 19.0);
    }

    #[test]
    fn shared_state_keeps_a_bounded_waveform() {
        let shared = Shared::new(VoltageHistory::load(None), false);
        for _ in 0..(MAX_CHART_POINTS + 15) {
            shared.record(&RealTimePowerState::default(), PackInfo::default());
        }

        let live = shared.live();
        assert_eq!(live.chart.len(), MAX_CHART_POINTS);
        assert!(live.state.is_some());
    }
}
