//! Live memory/CPU readout for the Settings window's resource-usage panel
//! (F20 in the project's feature list), sampled from this process's own
//! Win32 counters -- no external crate (e.g. `sysinfo`) needed for two
//! numbers this narrow.

use windows::Win32::Foundation::FILETIME;
use windows::Win32::System::ProcessStatus::{GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS};
use windows::Win32::System::Threading::{GetCurrentProcess, GetProcessTimes};

pub struct ResourceUsage {
    pub memory_mb: f64,
    /// Percent of one CPU core consumed since the last `sample()` call (not
    /// normalized by core count -- matches how this project's C# reference
    /// reported it, a simple "how busy is this process" number rather than a
    /// system-wide-normalized one).
    pub cpu_percent: f64,
}

fn filetime_to_u64(ft: FILETIME) -> u64 {
    ((ft.dwHighDateTime as u64) << 32) | ft.dwLowDateTime as u64
}

/// Holds the previous sample so `sample()` can compute a CPU-percent delta;
/// construct once and call `sample()` repeatedly (e.g. from a 1s UI timer).
pub struct ResourceMonitor {
    last_cpu_time_100ns: u64,
    last_wall: std::time::Instant,
}

impl ResourceMonitor {
    pub fn new() -> Self {
        Self { last_cpu_time_100ns: current_cpu_time_100ns(), last_wall: std::time::Instant::now() }
    }

    pub fn sample(&mut self) -> ResourceUsage {
        let memory_mb = current_working_set_bytes() as f64 / (1024.0 * 1024.0);

        let now_cpu = current_cpu_time_100ns();
        let now_wall = std::time::Instant::now();
        let wall_delta_100ns = now_wall.duration_since(self.last_wall).as_nanos() as f64 / 100.0;
        let cpu_delta_100ns = now_cpu.saturating_sub(self.last_cpu_time_100ns) as f64;
        let cpu_percent = if wall_delta_100ns > 0.0 { (cpu_delta_100ns / wall_delta_100ns) * 100.0 } else { 0.0 };

        self.last_cpu_time_100ns = now_cpu;
        self.last_wall = now_wall;

        ResourceUsage { memory_mb, cpu_percent: cpu_percent.clamp(0.0, 100.0 * num_cpus_best_effort()) }
    }
}

impl Default for ResourceMonitor {
    fn default() -> Self {
        Self::new()
    }
}

fn current_working_set_bytes() -> usize {
    unsafe {
        let mut counters = PROCESS_MEMORY_COUNTERS::default();
        let size = core::mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32;
        if GetProcessMemoryInfo(GetCurrentProcess(), &mut counters, size).is_ok() {
            counters.WorkingSetSize
        } else {
            0
        }
    }
}

fn current_cpu_time_100ns() -> u64 {
    unsafe {
        let mut creation = FILETIME::default();
        let mut exit = FILETIME::default();
        let mut kernel = FILETIME::default();
        let mut user = FILETIME::default();
        if GetProcessTimes(GetCurrentProcess(), &mut creation, &mut exit, &mut kernel, &mut user).is_ok() {
            filetime_to_u64(kernel) + filetime_to_u64(user)
        } else {
            0
        }
    }
}

fn num_cpus_best_effort() -> f64 {
    std::thread::available_parallelism().map(|n| n.get() as f64).unwrap_or(1.0)
}
