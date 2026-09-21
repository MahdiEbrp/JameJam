using JameJam;
using JameJam.Data;
using JameJam.Divan;
using JameJam.Ganjoor;
using JameJam.HaftKhan;
using JameJam.Raz;
using JameJam.Settings;
using JameJam.Soroush;
using JameJam.Taqvim;

using Microsoft.Extensions.DependencyInjection;

// ── Composition root ──
// The CLI layer is a thin DI shell: every service is registered here and injected into
// App. App itself stays constructor-injected and UI-free, so tests compose it with fakes.
var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

var services = new ServiceCollection();

// Storage (one SQLite file per service, env-overridable, created 0600).
services.AddSingleton<ISettingsStore>(_ => new SqliteSettingsStore(
    Environment.GetEnvironmentVariable("JAMEJAM_SETTINGS_DB") ?? Path.Combine(profile, ".jamejam", "settings.db")));
services.AddSingleton<ITaskRepository>(_ => new SqliteTaskRepository(
    Environment.GetEnvironmentVariable("JAMEJAM_HAFTKHAN_DB") ?? Path.Combine(profile, ".jamejam", "haftkhan.db")));
services.AddSingleton(sp => new SqliteGanjoorStore(
    Environment.GetEnvironmentVariable("JAMEJAM_GANJOOR_DB") ?? Path.Combine(profile, ".jamejam", "ganjoor.db")));
services.AddSingleton(sp => new SqliteVaultStore(
    Environment.GetEnvironmentVariable("JAMEJAM_RAZ_DB") ?? Path.Combine(profile, ".jamejam", "raz.db")));
services.AddSingleton(sp => new SqliteDivanStore(
    Environment.GetEnvironmentVariable("JAMEJAM_DIVAN_DB") ?? Path.Combine(profile, ".jamejam", "divan.db")));
services.AddSingleton(sp => new SqliteTaqvimStore(
    Environment.GetEnvironmentVariable("JAMEJAM_TAQVIM_DB") ?? Path.Combine(profile, ".jamejam", "taqvim.db")));

// The single AI funnel (provider/endpoint/model resolved per call from settings).
services.AddSingleton<Func<SoroushOptions, ISoroushClient>>(_ =>
    options => new SoroushClient(SoroushHttp.CreateClient(), options));

// The App — every service injected through its one constructor.
services.AddSingleton(sp => new App(
    Console.Out,
    Console.Error,
    sp.GetRequiredService<ISettingsStore>(),
    sp.GetRequiredService<Func<SoroushOptions, ISoroushClient>>(),
    sp.GetRequiredService<ITaskRepository>(),
    wallet: sp.GetRequiredService<SqliteGanjoorStore>(),
    razStore: sp.GetRequiredService<SqliteVaultStore>(),
    divanStore: sp.GetRequiredService<SqliteDivanStore>(),
    taqvimStore: sp.GetRequiredService<SqliteTaqvimStore>()));

using var provider = services.BuildServiceProvider();
return await provider.GetRequiredService<App>().RunAsync(args);
