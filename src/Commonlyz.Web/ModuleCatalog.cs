using System.Reflection;
using Commonlyz.Core.Modules;

namespace Commonlyz.Web;

/// <summary>Every app hosted by Commonlyz. Add a module here (and a project reference) to plug it in.</summary>
public static class ModuleCatalog
{
    public static readonly IReadOnlyList<IAppModule> All =
    [
    ];

    public static Assembly[] Assemblies => [.. All.Select(m => m.GetType().Assembly).Distinct()];
}
