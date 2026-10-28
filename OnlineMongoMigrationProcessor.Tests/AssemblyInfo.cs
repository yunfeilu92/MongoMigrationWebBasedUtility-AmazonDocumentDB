// Tests share static state (target CA, client cache), so run them one at a time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
