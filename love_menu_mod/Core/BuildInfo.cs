namespace FlyMod.Core
{
    // Dev builds (dotnet build -p:DevBuild=true, see build-dev.ps1) show the
    // developer tools: detailed logging, world script capture, the lag and
    // pose recorders, object name listing. Public releases hide them.
    internal static class BuildInfo
    {
#if DEV
        public static readonly bool Dev = true;
#else
        public static readonly bool Dev = false;
#endif
    }
}
