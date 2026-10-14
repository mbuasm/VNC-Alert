using MaszynyAlert.Core.Config;

namespace MaszynyAlert.App.Services;

public sealed record StartupConfig(AppConfig Base, AppConfig Effective);
