using Xunit;

// These tests are not independent of each other: several touch WPF, whose Application and
// Dispatcher are process-wide singletons, and others read live machine state (registry,
// services, power plans, sockets). Running collections in parallel lets a test on one thread
// wedge the UI dispatcher another collection is waiting on, which surfaces as an unrelated
// test timing out or as "Test host process crashed" with no failing test named.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
