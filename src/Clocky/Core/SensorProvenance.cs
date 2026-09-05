namespace Clocky.Core;

public enum SensorProvenance
{
    Unavailable = 0,
    NativeMSR = 1,
    DriverLHM = 2,
    DriverNVML = 3,
    MotherboardSuperIO = 4,
    KernelETW = 5,
    PerformanceCounter = 6,
    FallbackApproximation = 7
}

public enum GpuProcessEngineState
{
    Active = 0,
    CountersDisabled = 1,
    NoSupportedEngines = 2,
    Failed = 3
}
