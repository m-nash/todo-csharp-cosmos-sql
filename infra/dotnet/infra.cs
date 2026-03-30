#:package Azure.Provisioning@1.6.0-alpha.20260325.1
#:package Azure.Provisioning.AppService@1.4.0-beta.2
#:package Azure.Provisioning.CosmosDB@1.1.0-beta.1
#:package Azure.Provisioning.KeyVault@1.2.0-beta.1
#:package Azure.Provisioning.OperationalInsights@1.2.0-beta.1
#:package Azure.Provisioning.ApplicationInsights@1.2.0-beta.1

using Azure.Provisioning;
using Azure.Provisioning.AppService;
using Azure.Provisioning.ApplicationInsights;
using Azure.Provisioning.CosmosDB;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.OperationalInsights;
using Azure.Provisioning.Resources;
using System.Reflection;

// Output directory passed by azd as first argument
var outputDir = args.Length > 0 ? args[0] : "./generated";
Directory.CreateDirectory(outputDir);

var infra = new Infrastructure("main");

// ============================================================
// Log Analytics Workspace
// ============================================================
var logAnalytics = new OperationalInsightsWorkspace("logAnalytics")
{
    Sku = new OperationalInsightsWorkspaceSku
    {
        Name = OperationalInsightsWorkspaceSkuName.PerGB2018,
    },
};
infra.Add(logAnalytics);

// ============================================================
// Application Insights
// ============================================================
var appInsights = new ApplicationInsightsComponent("appInsights")
{
    Kind = "web",
    ApplicationType = ApplicationInsightsApplicationType.Web,
    WorkspaceResourceId = logAnalytics.Id,
};
infra.Add(appInsights);

// ============================================================
// App Service Plan (B3, Linux)
// ============================================================
var appServicePlan = new AppServicePlan("appServicePlan")
{
    Kind = "Linux",
    IsReserved = true,
    Sku = new AppServiceSkuDescription
    {
        Name = "B3",
        Tier = "Basic",
    },
};
infra.Add(appServicePlan);

// ============================================================
// Web App (React frontend, Node.js 20)
// ============================================================
var webApp = new WebSite("web")
{
    Kind = "app,linux",
    AppServicePlanId = appServicePlan.Id,
    SiteConfig = new SiteConfigProperties
    {
        IsAlwaysOn = true,
        LinuxFxVersion = "node|20-lts",
        AppCommandLine = "pm2 serve /home/site/wwwroot --no-daemon --spa",
    },
    Tags = { { "azd-service-name", "web" } },
};
infra.Add(webApp);

// ============================================================
// API App (.NET 8, managed identity)
// ============================================================
var apiApp = new WebSite("api")
{
    Kind = "app",
    AppServicePlanId = appServicePlan.Id,
    IsClientAffinityEnabled = true,
    Identity = new ManagedServiceIdentity
    {
        ManagedServiceIdentityType = ManagedServiceIdentityType.SystemAssigned,
    },
    SiteConfig = new SiteConfigProperties
    {
        IsAlwaysOn = true,
        LinuxFxVersion = "dotnetcore|8.0",
        Cors = new AppServiceCorsSettings
        {
            AllowedOrigins =
            {
                "https://portal.azure.com",
                "https://ms.portal.azure.com",
                BicepFunction.Interpolate($"https://{webApp.DefaultHostName}"),
            },
        },
        AppSettings =
        {
            new AppServiceNameValuePair { Name = "SCM_DO_BUILD_DURING_DEPLOYMENT", Value = "false" },
        },
    },
    Tags = { { "azd-service-name", "api" } },
};
infra.Add(apiApp);

// ============================================================
// Cosmos DB (NoSQL, Serverless)
// ============================================================
var cosmosAccount = new CosmosDBAccount("cosmos")
{
    Kind = CosmosDBAccountKind.GlobalDocumentDB,
    DatabaseAccountOfferType = CosmosDBAccountOfferType.Standard,
    ConsistencyPolicy = new ConsistencyPolicy
    {
        DefaultConsistencyLevel = DefaultConsistencyLevel.Session,
    },
    DisableLocalAuth = true,
    EnableAutomaticFailover = false,
    Locations =
    {
        new CosmosDBAccountLocation
        {
            LocationName = new IdentifierExpression("location"),
            FailoverPriority = 0,
            IsZoneRedundant = false,
        },
    },
    Capabilities =
    {
        new CosmosDBAccountCapability { Name = "EnableServerless" },
    },
    BackupPolicy = new PeriodicModeBackupPolicy
    {
        PeriodicModeProperties = new PeriodicModeProperties
        {
            BackupIntervalInMinutes = 240,
            BackupRetentionIntervalInHours = 8,
        },
    },
};
infra.Add(cosmosAccount);

var cosmosDatabase = new CosmosDBSqlDatabase("cosmosDatabase")
{
    Parent = cosmosAccount,
    Name = "Todo",
    Resource = new CosmosDBSqlDatabaseResourceInfo
    {
        DatabaseName = "Todo",
    },
};
infra.Add(cosmosDatabase);

var todoListContainer = new CosmosDBSqlContainer("todoListContainer")
{
    Parent = cosmosDatabase,
    Name = "TodoList",
    Resource = new CosmosDBSqlContainerResourceInfo
    {
        ContainerName = "TodoList",
        PartitionKey = new CosmosDBContainerPartitionKey
        {
            Paths = { "/id" },
            Kind = CosmosDBPartitionKind.Hash,
        },
    },
};
infra.Add(todoListContainer);

var todoItemContainer = new CosmosDBSqlContainer("todoItemContainer")
{
    Parent = cosmosDatabase,
    Name = "TodoItem",
    Resource = new CosmosDBSqlContainerResourceInfo
    {
        ContainerName = "TodoItem",
        PartitionKey = new CosmosDBContainerPartitionKey
        {
            Paths = { "/id" },
            Kind = CosmosDBPartitionKind.Hash,
        },
    },
};
infra.Add(todoItemContainer);

// ============================================================
// Key Vault
// ============================================================
var keyVault = new KeyVaultService("keyVault")
{
    Properties = new KeyVaultProperties
    {
        Sku = new KeyVaultSku
        {
            Family = KeyVaultSkuFamily.A,
            Name = KeyVaultSkuName.Standard,
        },
        TenantId = BicepFunction.GetSubscription().TenantId,
        EnableRbacAuthorization = true,
        EnabledForDeployment = false,
        EnabledForTemplateDeployment = false,
        EnablePurgeProtection = null,
    },
};
infra.Add(keyVault);

// Wire app settings that reference other resources into the API app
apiApp.SiteConfig.AppSettings.Add(
    new AppServiceNameValuePair { Name = "AZURE_KEY_VAULT_ENDPOINT", Value = keyVault.Properties.VaultUri });
apiApp.SiteConfig.AppSettings.Add(
    new AppServiceNameValuePair { Name = "AZURE_COSMOS_DATABASE_NAME", Value = "Todo" });
apiApp.SiteConfig.AppSettings.Add(
    new AppServiceNameValuePair { Name = "AZURE_COSMOS_ENDPOINT", Value = cosmosAccount.DocumentEndpoint });
apiApp.SiteConfig.AppSettings.Add(
    new AppServiceNameValuePair
    {
        Name = "API_ALLOW_ORIGINS",
        Value = BicepFunction.Interpolate($"https://{webApp.DefaultHostName}"),
    });

// ============================================================
// Cosmos DB RBAC: API gets "Data Contributor" role
// ============================================================
var cosmosRoleAssignment = new CosmosDBSqlRoleAssignment("cosmosDataContributor")
{
    Parent = cosmosAccount,
    PrincipalId = apiApp.Identity.PrincipalId,
    Scope = cosmosAccount.Id,
};
// RoleDefinitionId needs interpolation to append the role definition path
cosmosRoleAssignment.RoleDefinitionId = new InterpolatedStringExpression(new BicepExpression[]
{
    cosmosAccount.Id.Compile(),
    new StringLiteralExpression("/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"),
});
// Name is marked as output-only in Azure.Provisioning but is required for sqlRoleAssignments.
// Unlock via reflection and set to guid(cosmosAccountId, principalId).
var nameValue = cosmosRoleAssignment.Name;
var isOutputField = nameValue.GetType().BaseType!
    .GetField("_isOutput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
isOutputField.SetValue(nameValue, false);
cosmosRoleAssignment.Name.Assign(
    BicepFunction.CreateGuid(cosmosAccount.Id, apiApp.Id));
infra.Add(cosmosRoleAssignment);

// ============================================================
// Outputs (must match what the app code and hooks expect)
// ============================================================
infra.Add(new ProvisioningOutput("AZURE_COSMOS_ENDPOINT", typeof(string))
{
    Value = cosmosAccount.DocumentEndpoint,
});

infra.Add(new ProvisioningOutput("AZURE_COSMOS_DATABASE_NAME", typeof(string))
{
    Value = new StringLiteralExpression("Todo"),
});

infra.Add(new ProvisioningOutput("APPLICATIONINSIGHTS_CONNECTION_STRING", typeof(string))
{
    Value = appInsights.ConnectionString,
});

infra.Add(new ProvisioningOutput("AZURE_KEY_VAULT_ENDPOINT", typeof(string))
{
    Value = keyVault.Properties.VaultUri,
});

infra.Add(new ProvisioningOutput("AZURE_KEY_VAULT_NAME", typeof(string))
{
    Value = keyVault.Name,
});

infra.Add(new ProvisioningOutput("AZURE_LOCATION", typeof(string))
{
    Value = new IdentifierExpression("location"),
});

infra.Add(new ProvisioningOutput("API_BASE_URL", typeof(string))
{
    Value = BicepFunction.Interpolate($"https://{apiApp.DefaultHostName}"),
});

infra.Add(new ProvisioningOutput("REACT_APP_WEB_BASE_URL", typeof(string))
{
    Value = BicepFunction.Interpolate($"https://{webApp.DefaultHostName}"),
});

// Build and save
infra.Build().Save(outputDir);
Console.WriteLine($"Generated Bicep files to: {outputDir}");
