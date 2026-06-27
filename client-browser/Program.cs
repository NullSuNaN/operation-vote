using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;

namespace operation_vote.Interface.ClientBrowser
{
  public class Program
  {
    public static async Task Main(string[] args)
    {
      var builder = WebAssemblyHostBuilder.CreateDefault(args);

      // Map the root Razor layout component directly into the #app div element in index.html
      builder.RootComponents.Add<App>("#app");

      builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

      var host = builder.Build();
      try
      {
        await host.RunAsync();
      }
      catch (Exception ex)
      {
        var jsRuntime = host.Services.GetRequiredService<IJSRuntime>();
        await jsRuntime.InvokeVoidAsync("alert", $"Startup Crash: {ex.Message}\n\nDetails: {ex.StackTrace}");

        throw;
      }
    }
  }
}