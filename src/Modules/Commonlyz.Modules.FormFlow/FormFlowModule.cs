using Commonlyz.Core;
using Commonlyz.Core.Data;
using Commonlyz.Core.Modules;
using Commonlyz.Modules.FormFlow.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Commonlyz.Modules.FormFlow;

public sealed class FormFlowModule : IAppModule
{
    public const string Key = "formflow";

    public ModuleInfo Info { get; } = new(
        Key, "FormFlow", "Typeform", "One-question-at-a-time forms for intake and questionnaires.", "/formflow", "form", ModuleGroup.Workflows);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<FormFlowDb>(Key);
        services.AddSingleton<FormFlowService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/formflow/{id:int}/responses.csv", async (int id, FormFlowService forms) =>
            Results.File(Csv.ToBytes(await forms.ExportCsvAsync(id)), "text/csv", $"form-{id}-responses.csv"))
            .RequireAuthorization();

    public async Task<IReadOnlyList<ModuleStat>> GetStatsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<FormFlowDb>>().CreateDbContextAsync(cancellationToken);
        var forms = await db.Forms.CountAsync(cancellationToken);
        var responses = await db.Responses.CountAsync(cancellationToken);
        return [new("Forms", Format.Number(forms)), new("Responses", Format.Number(responses))];
    }
}
