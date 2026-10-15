// netstandard2.1 only (the project includes this folder for that target): the compiler turns a lock statement on a
// System.Threading.Lock into EnterScope/Dispose; here that is a Monitor lock.
namespace System.Threading;

internal sealed class Lock
{
    private readonly object _monitor = new();

    public Scope EnterScope()
    {
        Monitor.Enter(_monitor);
        return new Scope(_monitor);
    }

    public ref struct Scope
    {
        private readonly object _monitor;

        internal Scope(object monitor) => _monitor = monitor;

        public void Dispose() => Monitor.Exit(_monitor);
    }
}
