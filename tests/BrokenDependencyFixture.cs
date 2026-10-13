// Compile twice. The dependency is kept outside the test application's search path, so the second assembly
// stands for an unrelated server plugin whose types cannot be loaded.
#if DEPENDENCY
namespace SeedGuardMissingDependency { public sealed class Descriptor {} }
#else
namespace Mysqlx
{
    public static class MysqlxReflection
    {
        public static SeedGuardMissingDependency.Descriptor descriptor;
    }
}
#endif
