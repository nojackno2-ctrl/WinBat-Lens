//! 把即時狀態轉成畫面上的文字與語意顏色。與繪圖分開，才能不靠視窗就驗證
//! 「讀不到就說讀不到」這條規則。

use eframe::egui::Color32;

use super::theme::{AMBER, EMERALD, ROSE, SLATE};
use crate::i18n::is_english;
use crate::models::{PowerSupplyCapability, RealTimePowerState};

/// 能量流動畫的方向。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EnergyFlow {
    /// 流向電池（市電 → 電池）。
    Charging,
    /// 自電池流出。
    Discharging,
    /// 市電直供，電池待機。
    Idle,
}

/// 即時功耗主視覺區要顯示的內容。
#[derive(Debug, Clone, PartialEq)]
pub struct HeroStatus {
    pub wattage_text: String,
    pub color: Color32,
    /// 狀態徽章上的簡短標籤。
    pub label: String,
    /// 徽章提示：完整、誠實地說明這個數字的來源。
    pub tooltip: String,
    pub flow: EnergyFlow,
    pub stream_color: Color32,
    /// 以閃電（有功率流動）或插頭（市電待機）圖示呈現。
    pub is_plug_icon: bool,
}

pub fn hero_status(state: &RealTimePowerState) -> HeroStatus {
    let en = is_english();
    let pick = |zh: String, english: String| if en { english } else { zh };

    if state.is_charger_deficit {
        // Plugged in and still draining. The shortfall is painted in the
        // discharge colour.
        let w = state.discharge_rate_w;
        return HeroStatus {
            wattage_text: format!("-{w:.1} W"),
            color: ROSE,
            label: pick("供電不足 (電池補足)".into(), "Deficit (Battery Covering)".into()),
            tooltip: pick(
                format!("外接電源供電不足。電池補上 -{w:.1}W（電池實測）"),
                format!("Charger cannot keep up. Battery covering -{w:.1}W (measured at the pack)"),
            ),
            flow: EnergyFlow::Discharging,
            stream_color: ROSE,
            is_plug_icon: false,
        };
    }

    if !state.has_battery {
        return HeroStatus {
            wattage_text: "-- W".into(),
            color: SLATE,
            label: pick("市電直供 (無電池)".into(), "On AC (No Battery)".into()),
            tooltip: pick(
                "本機未偵測到電池，沒有電池端功率可量測。變壓器輸入功率 Windows 並未提供".into(),
                "No battery detected, so there is no pack-side power to measure. Adapter input is not exposed by Windows".into(),
            ),
            flow: EnergyFlow::Idle,
            stream_color: SLATE,
            is_plug_icon: true,
        };
    }

    if state.is_ac_online {
        if state.is_charging && state.is_charge_rate_measured {
            // Charging is a real measurement, so it belongs in the headline.
            let w = state.charging_rate_w;
            return HeroStatus {
                wattage_text: format!("+{w:.1} W"),
                color: EMERALD,
                label: pick("充電中 (實測)".into(), "Charging (Measured)".into()),
                tooltip: pick(
                    format!("電池充電中 +{w:.1}W（電池實測）"),
                    format!("Charging the battery at +{w:.1}W (measured at the pack)"),
                ),
                flow: EnergyFlow::Charging,
                stream_color: EMERALD,
                is_plug_icon: false,
            };
        }

        if state.is_charging {
            return HeroStatus {
                wattage_text: "-- W".into(),
                color: EMERALD,
                label: pick("充電中 (未回報功率)".into(), "Charging (Rate Unavailable)".into()),
                tooltip: pick(
                    "電池充電中。此電池未回報充電功率".into(),
                    "Charging. This battery does not report a charge rate".into(),
                ),
                flow: EnergyFlow::Charging,
                stream_color: EMERALD,
                is_plug_icon: false,
            };
        }

        return HeroStatus {
            wattage_text: "-- W".into(),
            color: EMERALD,
            label: pick("市電直供 (電池待機)".into(), "On AC (Battery Idle)".into()),
            tooltip: pick(
                "市電直供中，電池無充放電電流。此狀態下沒有可量測的功率".into(),
                "On AC, battery idle. No current in or out, so there is nothing to measure".into(),
            ),
            flow: EnergyFlow::Idle,
            stream_color: SLATE,
            is_plug_icon: true,
        };
    }

    // On battery the pack itself reports the draw.
    let w = state.discharge_rate_w;
    let (wattage_text, label, tooltip) = if state.is_discharge_rate_measured {
        (
            format!("-{w:.1} W"),
            pick("放電中 (全系統實測)".into(), "Discharging (Measured)".into()),
            pick(
                format!("電池放電中 (-{w:.1}W 電池實測，為全系統真實耗電)"),
                format!("Battery Discharging (-{w:.1}W measured at the pack, whole system)"),
            ),
        )
    } else {
        (
            "-- W".to_string(),
            pick("放電中 (未回報功率)".into(), "Discharging (Rate Unavailable)".into()),
            pick(
                "電池放電中。此電池未回報放電功率".into(),
                "Battery discharging. This battery does not report a discharge rate".into(),
            ),
        )
    };

    HeroStatus {
        wattage_text,
        color: ROSE,
        label,
        tooltip,
        flow: EnergyFlow::Discharging,
        stream_color: ROSE,
        is_plug_icon: false,
    }
}

/// 市電節點的 LED 顏色與狀態文字。
pub fn ac_node_status(state: &RealTimePowerState) -> (Color32, &'static str) {
    let en = is_english();
    if !state.is_ac_online {
        (SLATE, if en { "On Battery" } else { "純電池供電" })
    } else if state.is_charger_deficit {
        (AMBER, if en { "Deficit AC" } else { "供電不足" })
    } else {
        (EMERALD, if en { "AC Online" } else { "市電連接" })
    }
}

/// Coloured by which way the current is flowing: out of the pack is red, into
/// it is green, and a pack sitting idle on AC is neither.
pub fn flow_color(state: &RealTimePowerState) -> Color32 {
    if !state.has_battery {
        SLATE
    } else if state.is_charger_deficit {
        ROSE
    } else if state.is_charging {
        EMERALD
    } else if state.is_ac_online {
        AMBER
    } else {
        ROSE
    }
}

/// 電池剩餘電量的顏色。
pub fn battery_level_color(percent: i32) -> Color32 {
    if percent < 20 {
        ROSE
    } else if percent < 50 {
        AMBER
    } else {
        EMERALD
    }
}

/// Windows 對外接電源的判定；只有供電不足時才值得多佔一行。
pub fn charger_verdict(state: &RealTimePowerState) -> Option<&'static str> {
    (state.is_charger_deficit || state.supply_capability == PowerSupplyCapability::Inadequate).then(|| {
        if is_english() {
            "Windows reports the external supply as inadequate for this system"
        } else {
            "Windows 判定：外接電源供電能力不足以支撐目前的系統負載"
        }
    })
}

/// Pack temperature, asked of the battery driver itself. Where the firmware
/// does not implement the query, say so instead of displaying a "--" that
/// looks like a failed read.
pub fn temperature_line(state: &RealTimePowerState) -> String {
    let en = is_english();
    if !state.has_battery {
        return if en { "No battery detected on this machine" } else { "本機未偵測到電池" }.into();
    }
    if state.is_battery_temperature_measured {
        let c = state.battery_temperature_c;
        if en {
            format!("Pack temperature {c:.1} °C (battery driver)")
        } else {
            format!("電池溫度 {c:.1} °C（電池驅動實測）")
        }
    } else if en {
        "Voltage read from the battery driver; this pack reports no temperature".into()
    } else {
        "電壓讀自電池驅動；此電池未回報溫度".into()
    }
}

/// Live tips. Every wattage quoted here is a real reading; when nothing real
/// is available the tip says so rather than inventing a figure.
pub fn live_power_tip(state: &RealTimePowerState) -> String {
    let en = is_english();
    let dgpu = if state.is_dgpu_power_measured {
        if en {
            format!(" Discrete GPU {:.1} W.", state.dgpu_power_w)
        } else {
            format!(" 獨顯實測 {:.1} W。", state.dgpu_power_w)
        }
    } else {
        String::new()
    };
    let discharge = state.discharge_rate_w;

    if state.is_charger_deficit {
        return if en {
            format!("Plugged in but still discharging: the charger is short by at least {discharge:.1} W, which the battery is covering.{dgpu} This is common with USB-C charging. A PD charger rated below this machine's draw cannot sustain it. Use a higher-wattage charger or reduce load to charge while working.")
        } else {
            format!("已接上外接電源，但電池仍在放電：充電器至少差 {discharge:.1} W，缺口由電池補上。{dgpu}這是 USB-C 充電常見的情況。PD 充電器的瓦數低於本機耗電就無法維持供電。請改用瓦數更高的充電器，或降低負載才能邊用邊充。")
        };
    }

    if !state.has_battery {
        return if en {
            format!("No battery detected. Whole-system wattage is measured at the battery pack, so there is nothing to measure on this machine. Adapter input is not exposed by Windows.{dgpu}")
        } else {
            format!("本機未偵測到電池。整機功耗是在電池端量測的，因此這台機器沒有可量測的系統功率（變壓器輸入功率 Windows 並未提供）。{dgpu}")
        };
    }

    if state.is_ac_online {
        return if state.is_charging && state.is_charge_rate_measured {
            let (charge, eta) = (state.charging_rate_w, &state.estimated_time_remaining_text);
            if en {
                format!("On AC. Battery charging at {charge:.1} W (measured at the pack). {eta}.{dgpu}")
            } else {
                format!("市電供電中，電池充電功率 {charge:.1} W（電池實測）。{eta}。{dgpu}")
            }
        } else if en {
            format!("On AC, no current is flowing to or from the battery. There is no measurable system wattage in this state. Adapter input is not exposed by Windows.{dgpu}")
        } else {
            format!(
                "市電直供中，電池無充放電電流，此狀態下沒有可量測的系統功率（變壓器輸入功率 Windows 並未提供）。{dgpu}"
            )
        };
    }

    if !state.is_discharge_rate_measured {
        return if en {
            format!("On battery. This machine's battery does not report a discharge rate, so no wattage can be shown.{dgpu}")
        } else {
            format!("電池供電中。此裝置的電池未回報放電功率，因此無法顯示瓦數。{dgpu}")
        };
    }

    if discharge > 20.0 || state.dgpu_usage_percent > 40.0 {
        // Only quote the brightness when it was actually read.
        let brightness = if !state.is_brightness_measured {
            String::new()
        } else if en {
            format!(" (now {}%)", state.screen_brightness_percent)
        } else {
            format!("（目前 {}%）", state.screen_brightness_percent)
        };
        if en {
            format!("High draw: {discharge:.1} W measured at the battery for the whole machine.{dgpu} Lowering screen brightness{brightness} extends runtime.")
        } else {
            format!(
                "目前放電 {discharge:.1} W（電池實測，為整機真實耗電）。{dgpu}建議調低螢幕亮度{brightness}以延長續航。"
            )
        }
    } else if en {
        format!("On battery: {discharge:.1} W measured at the pack, the whole machine's real draw.{dgpu}")
    } else {
        format!("電池供電中，實測放電 {discharge:.1} W（量測自電池，即整機真實耗電）。{dgpu}")
    }
}

/// Vertical range of the voltage chart: padded around the measured span and
/// never narrower than one volt, so a flat curve is not blown up into noise.
pub fn voltage_axis(measured_min: f64, measured_max: f64) -> (f64, f64) {
    let span = (measured_max - measured_min).max(1.0);
    let padding = (span * 0.1).max(0.2);
    let mut axis_min = ((measured_min - padding) * 10.0).floor() / 10.0;
    let mut axis_max = ((measured_max + padding) * 10.0).ceil() / 10.0;

    if axis_max - axis_min < 1.0 {
        let center = (measured_min + measured_max) / 2.0;
        axis_min = ((center - 0.5) * 10.0).floor() / 10.0;
        axis_max = axis_min + 1.0;
    }

    (axis_min, axis_max)
}

/// Top of the waveform's shared watt scale. All three traces use it, so equal
/// wattages always land at equal heights.
pub fn waveform_scale(peak_w: f64) -> f64 {
    (peak_w * 1.15).max(35.0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn state() -> RealTimePowerState {
        RealTimePowerState::default()
    }

    #[test]
    fn deficit_is_shown_as_a_discharge_even_on_ac() {
        let status = hero_status(&RealTimePowerState {
            is_ac_online: true,
            is_charger_deficit: true,
            is_discharge_rate_measured: true,
            discharge_rate_w: 14.0,
            ..state()
        });

        assert_eq!(status.wattage_text, "-14.0 W");
        assert_eq!(status.color, ROSE);
        assert_eq!(status.flow, EnergyFlow::Discharging);
    }

    #[test]
    fn charging_headline_requires_a_measured_rate() {
        let measured = hero_status(&RealTimePowerState {
            is_ac_online: true,
            is_charging: true,
            is_charge_rate_measured: true,
            charging_rate_w: 56.0,
            ..state()
        });
        assert_eq!(measured.wattage_text, "+56.0 W");
        assert_eq!(measured.flow, EnergyFlow::Charging);

        let unknown = hero_status(&RealTimePowerState { is_ac_online: true, is_charging: true, ..state() });
        assert_eq!(unknown.wattage_text, "-- W");
        assert_eq!(unknown.flow, EnergyFlow::Charging);
    }

    #[test]
    fn idle_on_ac_and_unmeasured_discharge_show_no_wattage() {
        let idle = hero_status(&RealTimePowerState { is_ac_online: true, ..state() });
        assert_eq!(idle.wattage_text, "-- W");
        assert_eq!(idle.flow, EnergyFlow::Idle);
        assert!(idle.is_plug_icon);

        let on_battery = hero_status(&state());
        assert_eq!(on_battery.wattage_text, "-- W");
        assert_eq!(on_battery.flow, EnergyFlow::Discharging);
    }

    #[test]
    fn desktops_show_no_battery_instead_of_a_charging_state() {
        let desktop = RealTimePowerState { has_battery: false, is_ac_online: true, ..state() };
        let status = hero_status(&desktop);

        assert_eq!(status.wattage_text, "-- W");
        assert_eq!(status.flow, EnergyFlow::Idle);
        assert!(status.is_plug_icon);
        assert!(live_power_tip(&desktop).contains("未偵測到電池"));
    }

    #[test]
    fn charger_verdict_only_appears_when_supply_is_short() {
        assert!(charger_verdict(&state()).is_none());
        assert!(charger_verdict(&RealTimePowerState { supply_capability: PowerSupplyCapability::Adequate, ..state() })
            .is_none());
        assert!(charger_verdict(&RealTimePowerState {
            supply_capability: PowerSupplyCapability::Inadequate,
            ..state()
        })
        .is_some());
        assert!(charger_verdict(&RealTimePowerState { is_charger_deficit: true, ..state() }).is_some());
    }

    #[test]
    fn tips_never_quote_unmeasured_figures() {
        let tip = live_power_tip(&RealTimePowerState {
            discharge_rate_w: 30.0,
            is_discharge_rate_measured: true,
            screen_brightness_percent: 75,
            is_brightness_measured: false,
            dgpu_power_w: 12.0,
            is_dgpu_power_measured: false,
            ..state()
        });

        assert!(tip.contains("30.0 W"));
        assert!(!tip.contains("75%"));
        assert!(!tip.contains("12.0 W"));
    }

    #[test]
    fn voltage_axis_pads_and_keeps_a_minimum_range() {
        let (min, max) = voltage_axis(15.2, 15.2);
        assert!(min <= 15.0 + 1e-9 && max >= 15.4 - 1e-9);
        assert!(max - min >= 1.0 - 1e-9);

        let (min, max) = voltage_axis(12.0, 16.8);
        assert!((min - 11.5).abs() < 1e-9, "{min}");
        assert!((max - 17.3).abs() < 1e-9, "{max}");
    }

    #[test]
    fn waveform_scale_has_a_floor() {
        assert_eq!(waveform_scale(0.0), 35.0);
        assert_eq!(waveform_scale(20.0), 35.0);
        assert!((waveform_scale(100.0) - 115.0).abs() < 1e-9);
    }
}
