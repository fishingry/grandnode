﻿using Grand.Core;
using Grand.Core.Caching;
using Grand.Core.Configuration;
using Grand.Core.Data;
using Grand.Core.Plugins;
using Grand.Core.Extensions;
using Grand.Framework.Security;
using Grand.Services.Installation;
using Grand.Services.Logging;
using Grand.Services.Security;
using Grand.Web.Infrastructure.Installation;
using Grand.Web.Models.Install;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Authentication;
using System.Security.Principal;
using System.Threading.Tasks;
using Grand.Framework.Extensions;

namespace Grand.Web.Controllers
{
    public partial class InstallController : Controller
    {
        #region Fields

        private readonly IInstallationLocalizationService _locService;
        private readonly GrandConfig _config;
        private readonly ICacheManager _cacheManager;
        private readonly IServiceProvider _serviceProvider;

        #endregion

        #region Ctor

        public InstallController(IInstallationLocalizationService locService
            , GrandConfig config
            , ICacheManager cacheManager
            , IServiceProvider serviceProvider)
        {
            this._locService = locService;
            this._config = config;
            this._cacheManager = cacheManager;
            this._serviceProvider = serviceProvider;
        }

        #endregion

        #region Utilities

        /// <summary>
        /// A value indicating whether we use MARS (Multiple Active Result Sets)
        /// </summary>
        protected bool UseMars 
        {
            get { return false; }
        }


        #endregion

        #region Methods

        public virtual IActionResult Index()
        {
            if (DataSettingsHelper.DatabaseIsInstalled())
                return RedirectToRoute("HomePage");

            var settings = new DataSettingsManager().LoadSettings(reloadSettings: true);

            var model = new InstallModel {
                AdminEmail = settings.AdminEmail,
                InstallSampleData = settings.InstallSampleData,
                DatabaseConnectionString = settings.DataConnectionString,
                UseConnectionString = settings.UseConnectionString,
                DataProvider = settings.DataProvider,
                AdminPassword = settings.AdminPassword,
                Collation = settings.Collation,
                ConfirmPassword = settings.MongoDBPassword,
                MongoCredentialMechanism = settings.MongoCredentialMechanism,
                MongoDBDatabaseName = settings.MongoDBDatabaseName,
                MongoDBPassword = settings.MongoDBPassword,
                MongoDBServerName = settings.MongoDBServerName,
                MongoDBServerPort = settings.MongoDBServerPort,
                MongoDBUsername = settings.MongoDBUsername,
                ReplicaSet = settings.ReplicaSet,
                SslProtocol = settings.SslProtocol
            };

            foreach (var lang in _locService.GetAvailableLanguages())
            {
                model.AvailableLanguages.Add(new SelectListItem {
                    Value = Url.Action("ChangeLanguage", "Install", new { language = lang.Code }),
                    Text = lang.Name,
                    Selected = _locService.GetCurrentLanguage().Code == lang.Code,
                });
            }
            //prepare collation list
            foreach (var col in _locService.GetAvailableCollations())
            {
                model.AvailableCollation.Add(new SelectListItem {
                    Value = col.Value,
                    Text = col.Name,
                    Selected = _locService.GetCurrentLanguage().Code == col.Value,
                });
            }

            model.SslProtocols = SslProtocols.Tls12.ToSelectListItems();
            return View(model);
        }

        [HttpPost]
        public virtual async Task<IActionResult> Index(InstallModel model)
        {
            if (DataSettingsHelper.DatabaseIsInstalled())
                return RedirectToRoute("HomePage");

            if (model.DatabaseConnectionString != null)
                model.DatabaseConnectionString = model.DatabaseConnectionString.Trim();

            string connectionString = "";

            if (model.UseConnectionString)
            {
                if (string.IsNullOrEmpty(model.DatabaseConnectionString))
                {
                    ModelState.AddModelError("", _locService.GetResource("ConnectionStringRequired"));
                }
                else
                {
                    connectionString = model.DatabaseConnectionString;
                }
            }
            else
            {
                if (string.IsNullOrEmpty(model.MongoDBDatabaseName))
                {
                    ModelState.AddModelError("", _locService.GetResource("DatabaseNameRequired"));
                }
                if (string.IsNullOrEmpty(model.MongoDBServerName))
                {
                    ModelState.AddModelError("", _locService.GetResource("MongoDBServerNameRequired"));
                }
                if (string.IsNullOrEmpty(model.MongoDBPassword))
                {
                    ModelState.AddModelError("", _locService.GetResource("MongoDBPasswordRequired"));
                }
                if (model.MongoDBServerPort < 1)
                {
                    ModelState.AddModelError("", _locService.GetResource("MongoDBServerPortRequired"));
                }
                if (string.IsNullOrEmpty(model.MongoDBPassword))
                {
                    ModelState.AddModelError("", _locService.GetResource("MongoDBPasswordRequired"));
                }

                connectionString = $"mongodb://{model.MongoDBUsername}:{model.MongoDBPassword}@{model.MongoDBServerName}:{model.MongoDBServerPort}/{model.MongoDBDatabaseName}";
                var connParams = new List<string>();

                if (model.SslProtocol != SslProtocols.None)
                {
                    connParams.Add("ssl=true");
                }
                if (!string.IsNullOrEmpty(model.ReplicaSet))
                {
                    connParams.Add($"replicaSet={model.ReplicaSet}");
                }

                if (connParams.Count > 0)
                {
                    connectionString = $"{connectionString}?{string.Join("&", connParams)}";
                }

            }

            if (!string.IsNullOrEmpty(connectionString))
            {
                try
                {
                    MongoClient client;
                    if (model.UseConnectionString)
                    {
                        client = new MongoClient(model.DatabaseConnectionString);
                    }
                    else
                    {
                        MongoClientSettings settings = new MongoClientSettings();
                        settings.Server = new MongoServerAddress(model.MongoDBServerName, model.MongoDBServerPort);

                        if (model.SslProtocol != SslProtocols.None)
                        {
                            settings.UseSsl = true;
                            settings.SslSettings = new SslSettings();
                            settings.SslSettings.EnabledSslProtocols = model.SslProtocol;
                        }

                        MongoIdentity identity = new MongoInternalIdentity(model.MongoDBDatabaseName, model.MongoDBUsername);
                        MongoIdentityEvidence evidence = new PasswordEvidence(model.MongoDBPassword);

                        settings.Credential = new MongoCredential(model.MongoCredentialMechanism, identity, evidence);

                        client = new MongoClient(settings);
                    }

                    var database = client.GetDatabase();
                    database.RunCommandAsync((Command<BsonDocument>)"{ping:1}").Wait();

                    var filter = new BsonDocument("name", "GrandNodeVersion");
                    var found = database.ListCollectionsAsync(new ListCollectionsOptions { Filter = filter }).Result;

                    if (found.Any())
                        ModelState.AddModelError("", _locService.GetResource("AlreadyInstalled"));
                }
                catch (Exception ex)
                {
                    ModelState.AddModelError("", ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                }
            }
            else
                ModelState.AddModelError("", _locService.GetResource("ConnectionStringRequired"));

            var webHelper = _serviceProvider.GetRequiredService<IWebHelper>();

            //validate permissions
            var dirsToCheck = FilePermissionHelper.GetDirectoriesWrite();
            foreach (string dir in dirsToCheck)
                if (!FilePermissionHelper.CheckPermissions(dir, false, true, true, false))
                    ModelState.AddModelError("", string.Format(_locService.GetResource("ConfigureDirectoryPermissions"), WindowsIdentity.GetCurrent().Name, dir));

            var filesToCheck = FilePermissionHelper.GetFilesWrite();
            foreach (string file in filesToCheck)
                if (!FilePermissionHelper.CheckPermissions(file, false, true, true, true))
                    ModelState.AddModelError("", string.Format(_locService.GetResource("ConfigureFilePermissions"), WindowsIdentity.GetCurrent().Name, file));

            if (ModelState.IsValid)
            {
                var settingsManager = new DataSettingsManager();
                //save settings
                var settings = new DataSettings {
                    DataProvider = "mongodb",
                    DataConnectionString = connectionString,
                    MongoCredentialMechanism = model.MongoCredentialMechanism,
                    MongoDBDatabaseName = model.MongoDBDatabaseName,
                    MongoDBPassword = model.MongoDBPassword,
                    MongoDBServerName = model.MongoDBServerName,
                    MongoDBServerPort = model.MongoDBServerPort,
                    MongoDBUsername = model.MongoDBUsername,
                    SslProtocol = model.SslProtocol,
                    ReplicaSet = model.ReplicaSet,
                    UseConnectionString = model.UseConnectionString
                };
                try
                {
                    settingsManager.SaveSettings(settings);

                    var dataProviderInstance = _serviceProvider.GetRequiredService<BaseDataProviderManager>().LoadDataProvider();
                    dataProviderInstance.InitDatabase();

                    settings = settingsManager.LoadSettings(reloadSettings: true);

                    if (!settings.DatabaseInstalled)
                    {
                        //install Database and data
                        var installationService = _serviceProvider.GetRequiredService<IInstallationService>();
                        settings.DatabaseInstalled = false;
                        settingsManager.SaveSettings(settings);
                        await installationService.InstallData(model.AdminEmail, model.AdminPassword, model.Collation ?? "en", model.InstallSampleData);

                        settings.DatabaseInstalled = true;
                        settingsManager.SaveSettings(settings);
                    }

                    if (!settings.PluginsInstalled)
                    {
                        //install plugins
                        PluginManager.MarkAllPluginsAsUninstalled();
                        var pluginFinder = _serviceProvider.GetRequiredService<IPluginFinder>();
                        var plugins = pluginFinder.GetPlugins<IPlugin>(LoadPluginsMode.All)
                            .ToList()
                            .OrderBy(x => x.PluginDescriptor.Group)
                            .ThenBy(x => x.PluginDescriptor.DisplayOrder)
                            .ToList();

                        var pluginsIgnoredDuringInstallation = string.IsNullOrEmpty(_config.PluginsIgnoredDuringInstallation) ?
                            new List<string>() :
                            _config.PluginsIgnoredDuringInstallation
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(x => x.Trim())
                            .ToList();
                        foreach (var plugin in plugins)
                        {
                            if (pluginsIgnoredDuringInstallation.Contains(plugin.PluginDescriptor.SystemName))
                                continue;

                            try
                            {
                                await plugin.Install();
                            }
                            catch (Exception ex)
                            {
                                var _logger = _serviceProvider.GetRequiredService<ILogger>();
                                await _logger.InsertLog(Core.Domain.Logging.LogLevel.Error, "Error during installing plugin " + plugin.PluginDescriptor.SystemName,
                                    ex.Message + " " + ex.InnerException?.Message);
                            }
                        }

                        settings.PluginsInstalled = true;
                        settingsManager.SaveSettings(settings);
                    }

                    if (!settings.PermissionInstalled)
                    {
                        //register default permissions
                        var permissionProviders = new List<Type>();
                        permissionProviders.Add(typeof(StandardPermissionProvider));
                        foreach (var providerType in permissionProviders)
                        {
                            var provider = (IPermissionProvider)Activator.CreateInstance(providerType);
                            await _serviceProvider.GetRequiredService<IPermissionService>().InstallPermissions(provider);
                        }
                        //if no exception happens before, the installation is done.
                        settings.PermissionInstalled = true;
                        settingsManager.SaveSettings(settings);
                    }
                    //if no exception happens before, the installation is done.
                    settings.Installed = true;
                    settingsManager.SaveSettings(settings);

                    //reset cache
                    DataSettingsHelper.ResetCache();

                    //restart application
                    if (Core.OperatingSystem.IsWindows())
                    {
                        webHelper.RestartAppDomain();
                        //Redirect to home page
                        return RedirectToRoute("HomePage");
                    }
                    else
                    {
                        return View(new InstallModel() { Installed = true });
                    }
                }
                catch (Exception exception)
                {
                    settings.InstallMessage = exception.ToString();
                    settingsManager.SaveSettings(settings);
                    //reset cache
                    DataSettingsHelper.ResetCache();
                    await _cacheManager.Clear();
                    //System.IO.File.Delete(CommonHelper.MapPath("~/App_Data/Settings.txt"));

                    ModelState.AddModelError("", string.Format(_locService.GetResource("SetupFailed"), exception.Message + " " + exception.InnerException?.Message));
                }
            }

            //prepare language list
            foreach (var lang in _locService.GetAvailableLanguages())
            {
                model.AvailableLanguages.Add(new SelectListItem {
                    Value = Url.Action("ChangeLanguage", "Install", new { language = lang.Code }),
                    Text = lang.Name,
                    Selected = _locService.GetCurrentLanguage().Code == lang.Code,
                });
            }

            //prepare collation list
            foreach (var col in _locService.GetAvailableCollations())
            {
                model.AvailableCollation.Add(new SelectListItem {
                    Value = col.Value,
                    Text = col.Name,
                    Selected = _locService.GetCurrentLanguage().Code == col.Value,
                });
            }

            model.SslProtocols = SslProtocols.Tls12.ToSelectListItems();
            return View(model);
        }

        public virtual IActionResult ChangeLanguage(string language)
        {
            if (DataSettingsHelper.DatabaseIsInstalled())
                return RedirectToRoute("HomePage");

            _locService.SaveCurrentLanguage(language);

            //Reload the page
            return RedirectToAction("Index", "Install");
        }

        public virtual IActionResult RestartInstall()
        {
            if (DataSettingsHelper.DatabaseIsInstalled())
                return RedirectToRoute("HomePage");

            //restart application
            var webHelper = _serviceProvider.GetRequiredService<IWebHelper>();
            webHelper.RestartAppDomain();

            //Redirect to home page
            return RedirectToRoute("HomePage");
        }

        #endregion
    }
}
