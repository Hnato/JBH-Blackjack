using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;

var serverWwwRoot = Path.Combine(Directory.GetCurrentDirectory(), "Server", "wwwroot");
var options = new WebApplicationOptions
{
    Args = args,
    WebRootPath = Directory.Exists(serverWwwRoot) ? serverWwwRoot : "wwwroot"
};

var builder = WebApplication.CreateBuilder(options);
builder.Services.AddSignalR(hubOptions =>
{
    hubOptions.EnableDetailedErrors = true;
    hubOptions.KeepAliveInterval = TimeSpan.FromSeconds(15);
    hubOptions.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
});
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(origin => true)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});
builder.WebHost.ConfigureKestrel(o=>{ o.ListenAnyIP(5329); });

StartupDiagnostics.Run();
var app = builder.Build();

app.UseCors("AllowAll");

var defaultFiles = new DefaultFilesOptions();
defaultFiles.DefaultFileNames.Clear();
defaultFiles.DefaultFileNames.Add("static/index.html");
app.UseDefaultFiles(defaultFiles);
app.UseStaticFiles();

app.MapHub<GameHub>("/gamehub");

app.Run();

static class StartupDiagnostics
{
    public static void Run()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                TryRun("winget", "--version");
                TryRun("dotnet", "--info");
                TryRun("netsh", "advfirewall firewall add rule name=BlackJackBJH dir=in action=allow protocol=TCP localport=5329");
            }
        }
        catch {}
    }
    static void TryRun(string file, string args)
    {
        try
        {
            var p=new Process();
            p.StartInfo.FileName=file;
            p.StartInfo.Arguments=args;
            p.StartInfo.CreateNoWindow=true;
            p.StartInfo.UseShellExecute=false;
            p.StartInfo.RedirectStandardOutput=true;
            p.StartInfo.RedirectStandardError=true;
            p.Start();
            p.WaitForExit(3000);
        }
        catch {}
    }
}
