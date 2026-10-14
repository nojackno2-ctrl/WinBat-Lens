//! 自繪的儀表元件：健康度環形儀表、容量衰退條、能量流示意圖、波形圖與電壓曲線。
//! 全部以向量圖形即時繪製，不使用任何點陣素材。

use std::collections::VecDeque;
use std::f32::consts::{PI, TAU};

use eframe::egui::{
    self, epaint::Mesh, pos2, vec2, Align2, Color32, CornerRadius, FontId, Pos2, Rect, Sense, Shape, Stroke,
    StrokeKind, Ui,
};

use super::present::{self, EnergyFlow, HeroStatus};
use super::theme::*;
use crate::history::BatteryVoltageHistoryPoint;
use crate::i18n::tr;
use crate::models::RealTimePowerState;
use crate::monitor::{ChartSample, MAX_CHART_POINTS};

/// Points along a circular arc, clockwise from `start` to `end` (radians,
/// 0 = three o'clock).
fn arc_points(center: Pos2, radius: f32, start: f32, end: f32) -> Vec<Pos2> {
    let steps = (((end - start).abs() / TAU) * 96.0).ceil().max(2.0) as usize;
    (0..=steps)
        .map(|i| {
            let angle = start + (end - start) * i as f32 / steps as f32;
            center + radius * vec2(angle.cos(), angle.sin())
        })
        .collect()
}

/// 120px 雙環健康度儀表：外圈刻度環與依健康度著色的進度弧。
/// `percent` 為 None 時（無電池或無法判定）只畫底環與破折號。
pub fn health_ring(ui: &mut Ui, percent: Option<f64>, color: Color32) {
    let (rect, _) = ui.allocate_exact_size(vec2(124.0, 124.0), Sense::hover());
    if !ui.is_rect_visible(rect) {
        return;
    }
    let painter = ui.painter_at(rect);
    let center = rect.center();

    // 柔和的底光
    painter.circle_filled(center, 46.0, tint(color, 0x12));

    // 外圈刻度
    for tick in 0..60 {
        let angle = tick as f32 / 60.0 * TAU;
        let direction = vec2(angle.cos(), angle.sin());
        let major = tick % 5 == 0;
        let inner = if major { 54.0 } else { 56.5 };
        painter.line_segment(
            [center + direction * inner, center + direction * 60.0],
            Stroke::new(1.0, if major { BORDER_STRONG } else { BORDER }),
        );
    }

    // 底環與進度弧（自十二點鐘方向順時針）
    painter.circle_stroke(center, 46.0, Stroke::new(9.0, BG_RAISED));
    if let Some(percent) = percent {
        let fraction = (percent / 100.0).clamp(0.0, 1.0) as f32;
        if fraction > 0.0 {
            let start = -PI / 2.0;
            painter.add(Shape::line(arc_points(center, 46.0, start, start + fraction * TAU), Stroke::new(9.0, color)));
        }
    }

    match percent {
        Some(percent) => {
            let number = painter.text(
                center + vec2(-5.0, -2.0),
                Align2::CENTER_CENTER,
                format!("{percent:.1}"),
                FontId::proportional(27.0),
                color,
            );
            painter.text(
                pos2(number.right() + 2.0, number.bottom() - 5.0),
                Align2::LEFT_BOTTOM,
                "%",
                FontId::proportional(13.0),
                color,
            );
        }
        None => {
            painter.text(center, Align2::CENTER_CENTER, "-", FontId::proportional(27.0), color);
        }
    }
}

/// 設計容量 vs 滿電容量的雙段條：實色段是目前滿電容量，斜線段是已損耗的容量。
pub fn capacity_bar(ui: &mut Ui, health_ratio: Option<f64>, color: Color32) {
    let (rect, _) = ui.allocate_exact_size(vec2(ui.available_width(), 16.0), Sense::hover());
    if !ui.is_rect_visible(rect) {
        return;
    }
    let painter = ui.painter_at(rect);
    let radius = CornerRadius::same(4);
    painter.rect_filled(rect, radius, BG_INSET);

    let Some(ratio) = health_ratio else {
        painter.rect_filled(rect, radius, tint(SLATE, 0x40));
        return;
    };

    let ratio = ratio.clamp(0.05, 1.0) as f32;
    let split = rect.left() + rect.width() * ratio;
    let full = Rect::from_min_max(rect.min, pos2(split, rect.bottom()));
    painter.rect_filled(full, radius, color);

    if 1.0 - ratio > 0.01 {
        let loss = Rect::from_min_max(pos2(split, rect.top()), rect.max);
        painter.rect_filled(loss, radius, tint(ROSE, 0x1C));

        // 工程圖風格的斜線，標示磨耗區
        let hatch = painter.with_clip_rect(loss);
        let mut x = loss.left() - loss.height();
        while x < loss.right() {
            hatch.line_segment(
                [pos2(x, loss.bottom()), pos2(x + loss.height(), loss.top())],
                Stroke::new(1.0, tint(ROSE, 0x90)),
            );
            x += 6.0;
        }
    }

    painter.rect_stroke(rect, radius, Stroke::new(1.0, BORDER), StrokeKind::Inside);
}

/// 細長的負載條（獨顯使用率）。
pub fn load_bar(ui: &mut Ui, fraction: f32, color: Color32, width: f32) {
    let (rect, _) = ui.allocate_exact_size(vec2(width, 6.0), Sense::hover());
    let painter = ui.painter_at(rect);
    let radius = CornerRadius::same(3);
    painter.rect_filled(rect, radius, BG_INSET);
    let filled = Rect::from_min_size(rect.min, vec2(rect.width() * fraction.clamp(0.0, 1.0), rect.height()));
    if filled.width() > 0.5 {
        painter.rect_filled(filled, radius, color);
    }
}

/// 閃電圖示。
fn bolt_icon(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let p = |x: f32, y: f32| center + vec2(x, y) * size;
    // 兩個凸三角形拼成閃電，避免凹多邊形填色失真。
    painter.add(Shape::convex_polygon(vec![p(0.15, -1.0), p(-0.55, 0.15), p(0.05, 0.15)], color, Stroke::NONE));
    painter.add(Shape::convex_polygon(vec![p(-0.15, 1.0), p(0.55, -0.15), p(-0.05, -0.15)], color, Stroke::NONE));
}

/// 插頭圖示。
fn plug_icon(painter: &egui::Painter, center: Pos2, size: f32, color: Color32) {
    let stroke = Stroke::new(size * 0.22, color);
    let p = |x: f32, y: f32| center + vec2(x, y) * size;
    painter.line_segment([p(-0.35, -1.0), p(-0.35, -0.45)], stroke);
    painter.line_segment([p(0.35, -1.0), p(0.35, -0.45)], stroke);
    painter.rect_filled(Rect::from_min_max(p(-0.65, -0.45), p(0.65, 0.2)), CornerRadius::same(2), color);
    painter.line_segment([p(0.0, 0.2), p(0.0, 1.0)], stroke);
}

/// 沿管線移動的能量粒子。`phase` 0..1 為動畫進度；None 時不畫粒子。
fn conduit(painter: &egui::Painter, from: Pos2, to: Pos2, color: Color32, phase: Option<f32>) {
    painter.line_segment([from, to], Stroke::new(3.0, BG_RAISED));
    painter.extend(Shape::dashed_line(&[from, to], Stroke::new(1.0, tint(color, 0x70)), 4.0, 5.0));

    let Some(phase) = phase else { return };
    const PARTICLES: usize = 3;
    for index in 0..PARTICLES {
        let t = (phase + index as f32 / PARTICLES as f32).fract();
        // 進出端淡入淡出
        let fade = (t.min(1.0 - t) * 5.0).clamp(0.0, 1.0);
        let position = from + (to - from) * t;
        painter.circle_filled(position, 5.5, tint(color, (fade * 50.0) as u8));
        painter.circle_filled(position, 2.6, tint(color, (fade * 255.0) as u8));
    }
}

/// 即時功耗主視覺：市電節點 → 電源管理中樞（即時瓦數）→ 電池容器。
/// 粒子的方向就是能量實際流動的方向。
pub fn power_schematic(ui: &mut Ui, state: &RealTimePowerState, status: &HeroStatus, animate: bool) {
    let (rect, _) = ui.allocate_exact_size(vec2(ui.available_width(), 132.0), Sense::hover());
    if !ui.is_rect_visible(rect) {
        return;
    }
    let painter = ui.painter_at(rect);
    let mid_y = rect.top() + 56.0;

    let node_width = (rect.width() * 0.2).clamp(110.0, 160.0);
    let hub_width = (rect.width() * 0.32).clamp(190.0, 280.0);
    let ac_rect = Rect::from_center_size(pos2(rect.left() + node_width / 2.0, mid_y), vec2(node_width, 84.0));
    let battery_rect =
        Rect::from_center_size(pos2(rect.right() - node_width / 2.0 - 6.0, mid_y), vec2(node_width, 84.0));
    let hub_rect = Rect::from_center_size(pos2(rect.center().x, mid_y), vec2(hub_width, 96.0));

    // 動畫相位：每 1.6 秒走完一段管線
    let phase = animate.then(|| (ui.input(|input| input.time) / 1.6).fract() as f32);
    let (ac_phase, battery_phase) = match status.flow {
        EnergyFlow::Charging => (phase, phase),
        EnergyFlow::Discharging => (None, phase),
        EnergyFlow::Idle => (phase, None),
    };

    // 管線：市電 → 中樞
    let (ac_color, ac_label) = present::ac_node_status(state);
    conduit(
        &painter,
        pos2(ac_rect.right() + 4.0, mid_y),
        pos2(hub_rect.left() - 4.0, mid_y),
        if state.is_ac_online { status.stream_color } else { BORDER_STRONG },
        if state.is_ac_online { ac_phase } else { None },
    );

    // 管線：中樞 ↔ 電池（放電時粒子自電池流回中樞）
    let hub_edge = pos2(hub_rect.right() + 4.0, mid_y);
    let battery_edge = pos2(battery_rect.left() - 4.0, mid_y);
    let (from, to) =
        if status.flow == EnergyFlow::Discharging { (battery_edge, hub_edge) } else { (hub_edge, battery_edge) };
    conduit(&painter, from, to, status.stream_color, battery_phase);

    // 市電節點
    let node_radius = CornerRadius::same(10);
    painter.rect(ac_rect, node_radius, BG_RAISED, Stroke::new(1.0, BORDER), StrokeKind::Inside);
    plug_icon(
        &painter,
        ac_rect.center() + vec2(0.0, -14.0),
        13.0,
        if state.is_ac_online { ac_color } else { TEXT_DIM },
    );
    let led = pos2(ac_rect.center().x - 34.0, ac_rect.bottom() - 16.0);
    painter.circle_filled(led, 6.0, tint(ac_color, 0x30));
    painter.circle_filled(led, 3.5, ac_color);
    painter.text(led + vec2(9.0, 0.0), Align2::LEFT_CENTER, ac_label, FontId::proportional(11.5), ac_color);

    // 電源管理中樞：即時瓦數
    painter.rect(
        hub_rect,
        CornerRadius::same(12),
        tint(status.color, 0x12),
        Stroke::new(1.5, status.color),
        StrokeKind::Inside,
    );
    let icon_center = pos2(hub_rect.left() + 30.0, hub_rect.center().y);
    painter.circle_filled(icon_center, 17.0, tint(status.color, 0x26));
    if status.is_plug_icon {
        plug_icon(&painter, icon_center, 9.0, AMBER);
    } else {
        bolt_icon(&painter, icon_center, 11.0, status.color);
    }
    painter.text(
        pos2(hub_rect.center().x + 18.0, hub_rect.center().y - 9.0),
        Align2::CENTER_CENTER,
        &status.wattage_text,
        mono(32.0),
        status.color,
    );
    painter.text(
        pos2(hub_rect.center().x + 18.0, hub_rect.bottom() - 17.0),
        Align2::CENTER_CENTER,
        tr("CardTotalPower"),
        FontId::proportional(11.0),
        TEXT_MUTED,
    );

    // 電池容器：液面高度即電量
    let level_color = present::battery_level_color(state.battery_percent);
    let vessel = Rect::from_center_size(battery_rect.center() + vec2(-4.0, -8.0), vec2(node_width - 28.0, 44.0));
    painter.rect(battery_rect, node_radius, BG_RAISED, Stroke::new(1.0, BORDER), StrokeKind::Inside);
    painter.rect_filled(
        Rect::from_center_size(pos2(vessel.right() + 4.0, vessel.center().y), vec2(5.0, 16.0)),
        CornerRadius::same(2),
        BORDER_STRONG,
    );
    painter.rect(vessel, CornerRadius::same(7), BG_INSET, Stroke::new(1.5, BORDER_STRONG), StrokeKind::Inside);

    let fraction = if state.has_battery { (state.battery_percent as f32 / 100.0).clamp(0.0, 1.0) } else { 0.0 };
    let inner = vessel.shrink(3.5);
    let liquid = Rect::from_min_size(inner.min, vec2(inner.width() * fraction, inner.height()));
    if liquid.width() > 1.0 {
        painter.rect_filled(liquid, CornerRadius::same(4), tint(level_color, 0xC8));
        // 玻璃反光
        let shine = Rect::from_min_size(liquid.min, vec2(liquid.width(), liquid.height() * 0.38));
        painter.rect_filled(shine, CornerRadius::same(4), tint(Color32::WHITE, 0x26));

        // 充電時的液面流光
        if let (Some(phase), EnergyFlow::Charging) = (phase, status.flow) {
            let x = liquid.left() + liquid.width() * phase;
            let shimmer =
                Rect::from_center_size(pos2(x, liquid.center().y), vec2(10.0, liquid.height())).intersect(liquid);
            painter.rect_filled(shimmer, CornerRadius::same(3), tint(Color32::WHITE, 0x30));
        }
    }
    painter.text(
        vessel.center(),
        Align2::CENTER_CENTER,
        if state.has_battery { format!("{}%", state.battery_percent) } else { "--".to_string() },
        mono(15.0),
        if state.has_battery { TEXT_MAIN } else { TEXT_DIM },
    );
    let energy = if state.is_energy_measured { state.battery_energy_text.as_str() } else { "" };
    painter.text(
        pos2(battery_rect.center().x, battery_rect.bottom() - 13.0),
        Align2::CENTER_CENTER,
        energy,
        FontId::proportional(11.0),
        TEXT_MUTED,
    );
}

/// 圖表共用的背景格線：橫向粗虛線、縱向細虛線。
fn chart_grid(painter: &egui::Painter, plot: Rect, vertical_lines: std::ops::RangeInclusive<usize>, full: Rect) {
    let mut shapes = Vec::new();
    for i in 1..=3 {
        let y = full.top() + full.height() / 4.0 * i as f32;
        Shape::dashed_line_many(
            &[pos2(full.left(), y), pos2(full.right(), y)],
            Stroke::new(1.0, tint(SLATE, 0x20)),
            4.0,
            4.0,
            &mut shapes,
        );
    }
    for i in vertical_lines {
        let x = plot.left() + plot.width() / 10.0 * i as f32;
        Shape::dashed_line_many(
            &[pos2(x, plot.top()), pos2(x, plot.bottom())],
            Stroke::new(1.0, tint(SLATE, 0x15)),
            2.0,
            4.0,
            &mut shapes,
        );
    }
    painter.extend(shapes);
}

/// 一條數列：折線下方以漸層填滿（頂端半透明、底部全透明）。
fn series(painter: &egui::Painter, points: &[Pos2], baseline: f32, color: Color32) {
    if points.len() >= 2 {
        let mut mesh = Mesh::default();
        for point in points {
            mesh.colored_vertex(*point, tint(color, 0x48));
            mesh.colored_vertex(pos2(point.x, baseline), tint(color, 0x00));
        }
        for i in 0..points.len() as u32 - 1 {
            let top = i * 2;
            mesh.add_triangle(top, top + 1, top + 2);
            mesh.add_triangle(top + 1, top + 3, top + 2);
        }
        painter.add(Shape::mesh(mesh));
    }
    match points {
        [] => {}
        [only] => {
            painter.circle_filled(*only, 2.0, color);
        }
        _ => {
            painter.add(Shape::line(points.to_vec(), Stroke::new(2.0, color)));
        }
    }
}

/// Picks one wattage series out of a waveform sample.
type Trace = fn(&ChartSample) -> f64;

/// 60 秒即時功率波形。三條瓦數曲線共用同一個刻度，高度可以直接比較：
/// 例如 20 W 的獨顯功耗是 80 W 放電功率的四分之一，而不是半張圖。
pub fn waveform_chart(ui: &mut Ui, chart: &VecDeque<ChartSample>, height: f32) {
    let (rect, _) = ui.allocate_exact_size(vec2(ui.available_width(), height), Sense::hover());
    if !ui.is_rect_visible(rect) {
        return;
    }
    let painter = ui.painter_at(rect);
    painter.rect(rect, CornerRadius::same(8), BG_INSET, Stroke::new(1.0, BORDER_SUBTLE), StrokeKind::Inside);

    let plot = rect.shrink2(vec2(6.0, 8.0));
    chart_grid(&painter, plot, 1..=9, plot);

    let peak = chart.iter().map(ChartSample::peak).fold(0.0, f64::max);
    let scale = present::waveform_scale(peak);

    let to_points = |value: Trace| -> Vec<Pos2> {
        chart
            .iter()
            .enumerate()
            .map(|(i, sample)| {
                let x = plot.left() + i as f32 / (MAX_CHART_POINTS - 1) as f32 * plot.width();
                let y = plot.bottom() - (value(sample) / scale).clamp(0.0, 1.0) as f32 * plot.height();
                pos2(x, y)
            })
            .collect()
    };

    // A trace that has been zero for the whole window is left off rather than
    // drawn as a flat line along the axis.
    let traces: [(Trace, Color32); 3] = [(|s| s.gpu_w, GPU_BLUE), (|s| s.charge_w, EMERALD), (|s| s.discharge_w, ROSE)];
    for (value, color) in traces {
        if chart.iter().any(|sample| value(sample) > 0.0) {
            series(&painter, &to_points(value), plot.bottom(), color);
        }
    }

    // Axis labels go on last so the area fills never cover them.
    for (fraction, label) in [(1.0, "100%"), (0.75, "75%"), (0.5, "50%"), (0.25, "25%"), (0.0, "0%")] {
        let y = plot.bottom() - plot.height() * fraction as f32;
        let anchor = if fraction >= 1.0 {
            Align2::LEFT_TOP
        } else if fraction <= 0.0 {
            Align2::LEFT_BOTTOM
        } else {
            Align2::LEFT_CENTER
        };
        painter.text(
            pos2(plot.left() + 4.0, y),
            anchor,
            format!("{:.0} W ({label})", scale * fraction),
            mono(10.0),
            TEXT_DIM,
        );
    }
}

/// 圖例上的色點與文字。用在由右至左的版面，因此先放文字、再放色點。
pub fn legend_item(ui: &mut Ui, color: Color32, text: &str) {
    ui.label(muted(text));
    let (dot, _) = ui.allocate_exact_size(vec2(10.0, 10.0), Sense::hover());
    ui.painter().circle_filled(dot.center(), 4.0, color);
}

/// Draws the voltage curve against a fixed 0–100% horizontal axis. Missing
/// percentages are left as gaps rather than being interpolated into values
/// that the battery driver never reported.
pub fn voltage_chart(ui: &mut Ui, points: &[BatteryVoltageHistoryPoint], height: f32) {
    let (rect, _) = ui.allocate_exact_size(vec2(ui.available_width(), height), Sense::hover());
    if !ui.is_rect_visible(rect) {
        return;
    }
    let painter = ui.painter_at(rect);
    painter.rect(rect, CornerRadius::same(8), BG_INSET, Stroke::new(1.0, BORDER_SUBTLE), StrokeKind::Inside);

    // 左側留給電壓刻度，底部留給百分比刻度
    let plot = Rect::from_min_max(rect.min + vec2(54.0, 10.0), rect.max - vec2(12.0, 22.0));
    chart_grid(&painter, plot, 0..=10, plot);

    for i in (0..=10).step_by(2) {
        let x = plot.left() + plot.width() / 10.0 * i as f32;
        painter.text(pos2(x, plot.bottom() + 4.0), Align2::CENTER_TOP, format!("{}%", i * 10), mono(10.0), TEXT_DIM);
    }

    let axis = (!points.is_empty()).then(|| {
        let min = points.iter().map(|p| p.average_voltage_v).fold(f64::INFINITY, f64::min);
        let max = points.iter().map(|p| p.average_voltage_v).fold(f64::NEG_INFINITY, f64::max);
        present::voltage_axis(min, max)
    });

    for fraction in [1.0, 0.75, 0.5, 0.25, 0.0] {
        let y = plot.bottom() - plot.height() * fraction as f32;
        let label = match axis {
            Some((min, max)) => format!("{:.2} V", min + (max - min) * fraction),
            None => "-- V".into(),
        };
        painter.text(pos2(plot.left() - 6.0, y), Align2::RIGHT_CENTER, label, mono(10.0), TEXT_DIM);
    }

    let Some((axis_min, axis_max)) = axis else {
        painter.text(
            plot.center(),
            Align2::CENTER_CENTER,
            tr("VoltageHistoryEmpty"),
            FontId::proportional(12.5),
            TEXT_DIM,
        );
        return;
    };

    let position = |point: &BatteryVoltageHistoryPoint| {
        let x = plot.left() + point.battery_percent as f32 / 100.0 * plot.width();
        let normalized = ((point.average_voltage_v - axis_min) / (axis_max - axis_min)).clamp(0.0, 1.0);
        pos2(x, plot.bottom() - normalized as f32 * plot.height())
    };

    // Only adjacent percentages share a segment, so a gap in the curve stays
    // visibly unmeasured.
    let mut segment: Vec<Pos2> = Vec::new();
    let mut previous_percent = -2;
    let flush = |segment: &mut Vec<Pos2>| {
        if segment.len() >= 2 {
            painter.add(Shape::line(std::mem::take(segment), Stroke::new(2.5, ACCENT)));
        } else {
            segment.clear();
        }
    };

    for point in points {
        if point.battery_percent != previous_percent + 1 {
            flush(&mut segment);
        }
        segment.push(position(point));
        previous_percent = point.battery_percent;
    }
    flush(&mut segment);

    for point in points {
        painter.circle_filled(position(point), 3.0, ACCENT);
    }
}

/// 檢測進行中的環形載入動畫。
pub fn loading_spinner(ui: &mut Ui, animate: bool) {
    let (rect, _) = ui.allocate_exact_size(vec2(56.0, 56.0), Sense::hover());
    let painter = ui.painter_at(rect);
    let center = rect.center();
    painter.circle_stroke(center, 22.0, Stroke::new(4.0, BG_RAISED));

    let rotation = if animate { (ui.input(|input| input.time) * 3.2) as f32 } else { -PI / 2.0 };
    painter.add(Shape::line(arc_points(center, 22.0, rotation, rotation + TAU * 0.3), Stroke::new(4.0, ACCENT)));
    bolt_icon(&painter, center, 9.0, ACCENT);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn arc_points_span_the_requested_angles() {
        let points = arc_points(pos2(0.0, 0.0), 10.0, 0.0, PI);
        assert!(points.len() >= 3);

        let first = points.first().unwrap();
        let last = points.last().unwrap();
        assert!((first.x - 10.0).abs() < 1e-3 && first.y.abs() < 1e-3);
        assert!((last.x + 10.0).abs() < 1e-3 && last.y.abs() < 1e-3);
        assert!(points.iter().all(|p| (p.to_vec2().length() - 10.0).abs() < 1e-3));
    }
}
