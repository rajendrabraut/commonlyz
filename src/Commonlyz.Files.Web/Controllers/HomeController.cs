using Commonlyz.Files.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace Commonlyz.Files.Web.Controllers;

public sealed class HomeController : Controller
{
    [HttpGet("/")]
    public IActionResult Index() => View(ToolCatalog.All);

    [HttpGet("/{slug:regex(^[[a-z0-9-]]+$)}")]
    public IActionResult Tool(string slug) => ToolCatalog.Find(slug) is { } tool ? View(tool) : NotFound();

    [HttpGet("/privacy")]
    public IActionResult Privacy() => View();
}
