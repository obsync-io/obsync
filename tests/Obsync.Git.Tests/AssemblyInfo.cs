using Xunit;

// GitEnvironmentIsolationTests sets real, PROCESS-GLOBAL environment variables (GIT_DIR,
// GIT_CONFIG_PARAMETERS) because inheriting them is precisely the behaviour under test — nothing
// less faithful reproduces it, since ProcessStartInfo seeds its block from this process.
//
// Every other test in this assembly launches the same real git, so a variable set by one class
// while another is mid-run would redirect that run's repository or re-enable a transport it asserts
// is denied. xUnit parallelises across classes by default, which makes that a race rather than a
// possibility. Serialising the assembly is the only guarantee; it is four classes, so the cost is
// wall-clock only.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
