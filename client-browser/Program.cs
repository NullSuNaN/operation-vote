using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

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

      await builder.Build().RunAsync();
    }
  }
}