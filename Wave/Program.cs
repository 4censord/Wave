using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Tomlyn.Extensions.Configuration;
using Wave.Components;
using Wave.Components.Account;
using Wave.Data;
using Wave.Services;
using Wave.Utilities;

var logMessages = new List<string>();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration
	.AddJsonFile(Path.Combine(FileSystemService.ConfigurationDirectory, "config.json"), true, false)
	.AddYamlFile(Path.Combine(FileSystemService.ConfigurationDirectory, "config.yml"), true, false)
	.AddTomlFile(Path.Combine(FileSystemService.ConfigurationDirectory, "config.toml"), true, false)
	.AddIniFile( Path.Combine(FileSystemService.ConfigurationDirectory, "config.ini"), true, false)
	.AddXmlFile( Path.Combine(FileSystemService.ConfigurationDirectory, "config.xml"), true, false)
	.AddEnvironmentVariables("WAVE_");

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddControllers(options => {
	options.OutputFormatters.Add(new SyndicationFeedFormatter());
});
builder.Services.AddOutputCache();

#region Data Protection & Redis

if (builder.Configuration.GetConnectionString("Redis") is { } redisUri) {
	var redis = ConnectionMultiplexer.Connect(redisUri);
	builder.Services.AddDataProtection()
		.PersistKeysToStackExchangeRedis(redis)
		.UseCryptographicAlgorithms(new AuthenticatedEncryptorConfiguration() {
			EncryptionAlgorithm = EncryptionAlgorithm.AES_256_CBC,
			ValidationAlgorithm = ValidationAlgorithm.HMACSHA256
		});
	builder.Services.AddStackExchangeRedisCache(options => {
		options.Configuration = redisUri;
		options.InstanceName = "WaveDistributedCache";
	});
	builder.Services.AddStackExchangeRedisOutputCache(options => {
		options.Configuration = redisUri;
		options.InstanceName = "WaveOutputCache";
	});
} else {
	builder.Services.AddDataProtection()
		.UseCryptographicAlgorithms(new AuthenticatedEncryptorConfiguration() {
			EncryptionAlgorithm = EncryptionAlgorithm.AES_256_CBC,
			ValidationAlgorithm = ValidationAlgorithm.HMACSHA256
		});
	logMessages.Add("No Redis connection string found.");
}

#endregion

#region Authentication & Authorization

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityUserAccessor>();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();

// Authors: Can create Articles, require them to be reviewed
// Reviewers: Can review Articles, but cannot create them themselves
// Moderators: Can delete Articles / take them Offline
// Admins: Can do anything, and assign roles to other users
builder.Services.AddAuthorizationBuilder()
	.AddPolicy("ArticleEditPermissions", p => p.RequireRole("Author", "Admin"))
	.AddPolicy("ArticleReviewPermissions", p => p.RequireRole("Reviewer", "Admin"))
	.AddPolicy("ArticleDeletePermissions", p => p.RequireRole("Moderator", "Admin"))
	.AddPolicy("CategoryManagePermissions", p => p.RequireRole("Admin"))
	.AddPolicy("RoleAssignPermissions", p => p.RequireRole("Admin"))
	
	.AddPolicy("ArticleEditOrReviewPermissions", p => p.RequireRole("Author", "Reviewer", "Admin"));
builder.Services.AddAuthentication(options => {
		options.DefaultScheme = IdentityConstants.ApplicationScheme;
		options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
	}).AddIdentityCookies();

#endregion

#region Identity

string connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
						  ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
	options.UseNpgsql(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = true)
	.AddRoles<IdentityRole>()
	.AddEntityFrameworkStores<ApplicationDbContext>()
	.AddSignInManager()
	.AddDefaultTokenProviders()
	.AddClaimsPrincipalFactory<UserClaimsFactory>();

#endregion

#region Services

builder.Services.AddHealthChecks();
builder.Services.AddLocalization(options => {
	options.ResourcesPath = "Resources";
});
builder.Services.AddScoped<ImageService>();
builder.Services.AddHttpClient();

builder.Services.Configure<Features>(builder.Configuration.GetSection(nameof(Features)));
builder.Services.Configure<Customization>(builder.Configuration.GetSection(nameof(Customization)));
builder.Services.AddCascadingValue("TitlePrefix", 
	sf => (sf.GetService<IOptions<Customization>>()?.Value.AppName ?? "Wave") + " - ");

var smtpConfig = builder.Configuration.GetSection("Email:Smtp");
if (smtpConfig.Exists()) {
	builder.Services.Configure<SmtpConfiguration>(smtpConfig);
	builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
	builder.Services.AddScoped<IAdvancedEmailSender, SmtpEmailSender>();
	builder.Services.AddScoped<IEmailSender<ApplicationUser>, SmtpEmailSender>();
} else {
	builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
	logMessages.Add("No Email provider configured.");
}

builder.Services.AddSingleton<IMessageDisplay, MessageService>();
builder.Services.AddSingleton<FileSystemService>();
builder.Services.AddSingleton<EmailTemplateService>();
builder.Services.AddHostedService<EmailBackgroundWorker>();

#endregion


string[] cultures = ["en-US", "en-GB", "de-DE"];
builder.Services.Configure<RequestLocalizationOptions>(options => {
	options.ApplyCurrentCultureToResponseHeaders = true;
	options.FallBackToParentCultures = true;
	options.FallBackToParentUICultures = true;
	options.SetDefaultCulture(cultures[0])
		.AddSupportedCultures(cultures)
		.AddSupportedUICultures(cultures);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment()) {
	app.UseMigrationsEndPoint();
} else {
	app.UseExceptionHandler("/Error", createScopeForErrors: true);
}


app.UseStaticFiles(new StaticFileOptions {
	ContentTypeProvider = new FileExtensionContentTypeProvider {
		Mappings = {
			[".jxl"] = "image/jxl"
		}
	}
});
app.UseAntiforgery();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.MapHealthChecks("/health");

app.MapControllers();
app.UseOutputCache();

app.UseRequestLocalization();

foreach (string message in logMessages) {
	app.Logger.LogInformation("{message}", message);
}

{
	using var scope = app.Services.CreateScope();
	using var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
	context.Database.Migrate();

	var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
	if (userManager.GetUsersInRoleAsync("Admin").Result.Any() is false) {
		string admin = Guid.NewGuid().ToString("N")[..16];
		app.Logger.LogWarning("There is currently no user in your installation with the admin role, " +
							  "go to /Admin and use the following password to self promote your account: {admin}", admin);
		File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "admin.txt"), admin);
	}
}

app.Run();
