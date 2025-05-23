using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Uno.Extensions.Hosting;

namespace Uno.Extensions.Maui;

/// <summary>
/// Embedding support for Microsoft.Maui controls in Uno Platform app hosts.
/// </summary>
public static partial class MauiEmbedding
{
	/// <summary>
	/// Registers Maui embedding in the Uno Platform app builder.
	/// </summary>
	/// <returns>The updated app builder.</returns>
	/// <param name="builder">The IHost builder.</param>
	/// <param name="configure">Optional lambda to configure the Maui app builder.</param>
	public static IApplicationBuilder UseMauiEmbedding<TApp>(this IApplicationBuilder builder, Action<MauiAppBuilder>? configure = null)
		where TApp : MauiApplication
		=> builder.Configure(hostBuilder => hostBuilder.UseMauiEmbedding<TApp>(builder.App, builder.Window, configure));

	/// <summary>
	/// Registers Maui embedding in the Uno Platform app builder.
	/// </summary>
	/// <returns>The updated app builder.</returns>
	/// <param name="builder">The IHost builder.</param>
	/// <param name="app">The Uno app.</param>
	/// <param name="window">The Main Application Window.</param>
	/// <param name="configure">Optional lambda to configure the Maui app builder.</param>
	public static IHostBuilder UseMauiEmbedding<TApp>(this IHostBuilder builder, Microsoft.UI.Xaml.Application app, Microsoft.UI.Xaml.Window window, Action<MauiAppBuilder>? configure = null)
		where TApp : MauiApplication
	{
		var mauiAppBuilder = ConfigureMauiAppBuilder<TApp>(app, window, configure);
		builder.UseServiceProviderFactory(new UnoServiceProviderFactory(mauiAppBuilder, () => BuildMauiApp(mauiAppBuilder, app, window)));
		return builder;
	}

	/// <summary>
	/// Registers Maui embedding with WinUI3 and WPF application builder.
	/// </summary>
	/// <param name="app">The Uno app.</param>
	/// <param name="window">The Main Application Window.</param>
	/// <param name="configure">Optional lambda to configure the Maui app builder.</param>
	public static MauiApp UseMauiEmbedding<TApp>(this Microsoft.UI.Xaml.Application app, Microsoft.UI.Xaml.Window window, Action<MauiAppBuilder>? configure = null)
		where TApp : MauiApplication
	{
		var mauiAppBuilder = ConfigureMauiAppBuilder<TApp>(app, window, configure);
		return BuildMauiApp(mauiAppBuilder, app, window);
	}

	private static MauiAppBuilder ConfigureMauiAppBuilder<TApp>(Application app, Microsoft.UI.Xaml.Window window, Action<MauiAppBuilder>? configure)
		where TApp : MauiApplication
	{
		// Forcing hot reload to false to prevent exceptions being raised
		Microsoft.Maui.HotReload.MauiHotReloadHelper.IsEnabled = false;

		var mauiAppBuilder = MauiApp.CreateBuilder()
			.UseMauiEmbedding<TApp>()
			.RegisterPlatformServices(app);

		mauiAppBuilder.Services.AddSingleton(app)
			.AddSingleton(window)
			.AddSingleton<IMauiInitializeService, MauiEmbeddingInitializer>();

		// HACK: https://github.com/dotnet/maui/pull/16758
		mauiAppBuilder.Services.RemoveAll<IApplication>()
			.AddSingleton<IApplication, TApp>();

		configure?.Invoke(mauiAppBuilder);

		return mauiAppBuilder;
	}

	private static MauiApp BuildMauiApp(MauiAppBuilder builder, Application app, Microsoft.UI.Xaml.Window window)
	{
		var mauiApp = builder.Build();
		mauiApp.InitializeMauiEmbeddingApp(app);

#if WINDOWS
		window.Activated += (s, args) =>
		{
			WindowStateManager.Default.OnActivated(window, args);
		};
#endif
		return mauiApp;
	}

#if !ANDROID && !IOS && !WINDOWS
	private static MauiAppBuilder RegisterPlatformServices(this MauiAppBuilder builder, Application app)
	{
		return builder;
	}

	private static void InitializeMauiEmbeddingApp(this MauiApp mauiApp, Application app)
	{
		var rootContext = new MauiContext(mauiApp.Services);
		rootContext.InitializeScopedServices();

		var iApp = mauiApp.Services.GetRequiredService<IApplication>();
		_ = new EmbeddedApplication(mauiApp.Services, iApp);
		app.SetApplicationHandler(iApp, rootContext);
		InitializeApplicationMainPage(iApp);
	}
#endif

	private static void InitializeScopedServices(this IMauiContext scopedContext)
	{
		var scopedServices = scopedContext.Services.GetServices<IMauiInitializeScopedService>();

		foreach (var service in scopedServices)
		{
			service.Initialize(scopedContext.Services);
		}
	}

	private static void InitializeApplicationMainPage(IApplication iApp)
	{
		if (iApp is not MauiApplication app || app.Handler?.MauiContext is null)
		{
			// NOTE: This method is supposed to be called immediately after we initialize the Application Handler
			// This should never actually happen but is required due to nullability
			return;
		}

#if ANDROID
		var services = app.Handler.MauiContext.Services;
		var context = new MauiContext(services, services.GetRequiredService<Android.App.Activity>());
#else
		var context = app.Handler.MauiContext;
#endif

		// Create an Application Main Page and initialize a Handler with the Maui Context
		var page = new ContentPage();
		app.MainPage = page;
		_ = page.ToPlatform(context);

		// Create a Maui Window and initialize a Handler shim. This will expose the actual Application Window
		var virtualWindow = new Microsoft.Maui.Controls.Window();
		virtualWindow.Handler = new EmbeddedWindowHandler
		{
#if IOS || MACCATALYST
			PlatformView = context.Services.GetRequiredService<UIKit.UIWindow>(),
#elif ANDROID
			PlatformView = context.Services.GetRequiredService<Android.App.Activity>(),
#elif WINDOWS
			PlatformView = context.Services.GetRequiredService<Microsoft.UI.Xaml.Window>(),
#endif
			VirtualView = virtualWindow,
			MauiContext = context
		};
		virtualWindow.Page = page;

		app.SetCoreWindow(virtualWindow);
	}

	private static void SetCoreWindow(this IApplication app, Microsoft.Maui.Controls.Window window)
	{
		if (app.Windows is List<Microsoft.Maui.Controls.Window> windows)
		{
			windows.Add(window);
		}
	}

	internal record EmbeddedApplication : IPlatformApplication
	{
		public EmbeddedApplication(IServiceProvider services, IApplication application)
		{
			Services = services;
			Application = application;
			IPlatformApplication.Current = this;
		}

		public IServiceProvider Services { get; }
		public IApplication Application { get; }
	}

	// NOTE: This was part of the POC and is out of scope for the MVP. Keeping it in case we want to add it back later.
	/*
	public static MauiAppBuilder MapControl<TWinUI, TMaui>(this MauiAppBuilder builder)
		where TWinUI : FrameworkElement
		where TMaui : Microsoft.Maui.Controls.View
	{
		Interop.MauiInterop.MapControl<TWinUI, TMaui>();
		return builder;
	}
	public static MauiAppBuilder MapStyleHandler<THandler>(this MauiAppBuilder builder)
		where THandler : Interop.IWinUIToMauiStyleHandler, new()
	{
		Interop.MauiInterop.MapStyleHandler<THandler>();
		return builder;
	}
	*/
}
