using System.Xml.Linq;
using Delivery.Modules.Identity.Domain;
namespace Delivery.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Domain_has_no_infrastructure_or_framework_dependencies()
    {
        var references = typeof(OtpPolicy).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        Assert.DoesNotContain(references, n => n.StartsWith("Npgsql") || n.StartsWith("Microsoft.AspNetCore")
            || n.StartsWith("Delivery.BuildingBlocks") || n == "Delivery.Api");
    }

    [Fact]
    public void Project_dependencies_follow_the_modular_boundary()
    {
        var root = FindRoot();
        var allowed = new Dictionary<string, string[]>
        {
            ["Delivery.Api"] = ["Delivery.Modules.Identity", "Delivery.Modules.Configuration", "Delivery.Modules.Customers", "Delivery.Modules.Deliveries", "Delivery.Modules.Dispatch", "Delivery.Modules.Ledger", "Delivery.Modules.Merchants", "Delivery.Modules.Notifications", "Delivery.Modules.Orders", "Delivery.Modules.Payments", "Delivery.Modules.Pricing", "Delivery.Modules.Support"],
            ["Delivery.BuildingBlocks"] = [],
            ["Delivery.Contracts"] = [],
            ["Delivery.Modules.Identity"] = ["Delivery.BuildingBlocks", "Delivery.Modules.Identity.Domain", "Delivery.Contracts"],
            ["Delivery.Modules.Identity.Domain"] = []
        };
        foreach (var module in new[] { "Configuration", "Customers", "Deliveries", "Dispatch", "Ledger", "Merchants", "Notifications", "Orders", "Payments", "Pricing", "Support" })
            allowed[$"Delivery.Modules.{module}"] = ["Delivery.BuildingBlocks", "Delivery.Contracts"];
        foreach (var project in Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var references = XDocument.Load(project).Descendants("ProjectReference")
                .Select(r => Path.GetFileNameWithoutExtension(r.Attribute("Include")!.Value.Replace('\\', '/'))).ToArray();
            Assert.All(references, reference => Assert.Contains(reference, allowed[name]));
            if (name.EndsWith(".Domain"))
                Assert.Empty(XDocument.Load(project).Descendants("PackageReference"));
        }
    }

    private static string FindRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Delivery.slnx"))) folder = folder.Parent;
        return folder?.FullName ?? throw new InvalidOperationException("Solution root missing.");
    }

    [Fact]
    public void Mvp_migration_scripts_are_available_in_the_published_module_assemblies()
    {
        foreach(var name in new[]{"Configuration","Customers","Merchants","Pricing","Orders","Payments","Dispatch","Deliveries","Ledger","Support","Notifications"})
        {
            var assembly=System.Reflection.Assembly.Load("Delivery.Modules."+name);
            Assert.Contains($"Delivery.Modules.{name}.Infrastructure.001.sql",assembly.GetManifestResourceNames());
            using var stream=assembly.GetManifestResourceStream($"Delivery.Modules.{name}.Infrastructure.001.sql");
            Assert.NotNull(stream);Assert.True(stream.Length>0);
        }
        Assert.Contains("Delivery.Modules.Payments.Infrastructure.002.sql",System.Reflection.Assembly.Load("Delivery.Modules.Payments").GetManifestResourceNames());
    }
}

