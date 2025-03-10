using Newtonsoft.Json;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Tebex.Adapters;
using Tebex.API;
using Tebex.Triage;

namespace Oxide.Plugins
{
    [Info("Tebex", "Tebex", "2.0.13")]
    [Description("Official support for the Tebex server monetization platform")]
    public class TebexPlugin : CovalencePlugin
    {
        private static TebexOxideAdapter _adapter;

        private Dictionary<string, DateTime> _lastNoteGiven = new Dictionary<string, DateTime>();

        public static string GetPluginVersion()
        {
            return "2.0.13";
        }

        public TebexPlatform GetPlatform(IServer server)
        {
            String gameId = "";
            
            #if RUST
                gameId = "Rust";
            #else
                gameId = "7 Days";
            #endif
            
            return new TebexPlatform(gameId, GetPluginVersion(), new TebexTelemetry("Oxide", server.Version, server.Protocol));
        }
        
        
        private void Init()
        {
            // Setup our API and adapter
            _adapter = new TebexOxideAdapter(this);
            TebexApi.Instance.InitAdapter(_adapter);

            BaseTebexAdapter.PluginConfig = Config.ReadObject<BaseTebexAdapter.TebexConfig>();
            if (!Config.Exists())
            {
                //Creates new config file
                LoadConfig();
            }

            // Register permissions
            permission.RegisterPermission("tebexplugin.secret", this);
            permission.RegisterPermission("tebexplugin.sendlink", this);
            permission.RegisterPermission("tebexplugin.forcecheck", this);
            permission.RegisterPermission("tebexplugin.refresh", this);
            permission.RegisterPermission("tebexplugin.report", this);
            permission.RegisterPermission("tebexplugin.ban", this);
            permission.RegisterPermission("tebexplugin.lookup", this);
            permission.RegisterPermission("tebexplugin.debug", this);
            permission.RegisterPermission("tebexplugin.setup", this);

            // Register user permissions
            permission.RegisterPermission("tebexplugin.info", this);
            permission.RegisterPermission("tebexplugin.categories", this);
            permission.RegisterPermission("tebexplugin.packages", this);
            permission.RegisterPermission("tebexplugin.checkout", this);

            // Check if auto reporting is disabled and show a warning if so.
            if (!BaseTebexAdapter.PluginConfig.AutoReportingEnabled)
            {
                _adapter.LogWarning("Auto reporting issues to Tebex is disabled.", "To enable, please set 'AutoReportingEnabled' to 'true' in config/Tebex.json");
                PluginEvent.IS_DISABLED = true;
            }

            // Check if secret key has been set. If so, get store information and place in cache
            if (BaseTebexAdapter.PluginConfig.SecretKey != "your-secret-key-here")
            {
                _adapter.FetchStoreInfo((info =>
                {
                    PluginEvent.SERVER_IP = server.Address.ToString();
                    PluginEvent.SERVER_ID = info.ServerInfo.Id.ToString();
                    PluginEvent.STORE_URL = info.AccountInfo.Domain;
                    new PluginEvent(this, this.GetPlatform(server), EnumEventLevel.INFO, "Server Init").Send(_adapter);
                    _adapter.SetSecretKeyValidated(true);
                }));
                return;
            }

            _adapter.LogInfo("Tebex detected a new configuration file.");
            _adapter.LogInfo("Use tebex:secret <secret> to add your store's secret key.");
            _adapter.LogInfo("Alternatively, add the secret key to 'Tebex.json' and reload the plugin.");
        }

        public WebRequests WebRequests()
        {
            return webrequest;
        }

        public IPlayerManager PlayerManager()
        {
            return players;
        }

        public PluginTimers PluginTimers()
        {
            return timer;
        }

        public IServer Server()
        {
            return server;
        }

        public string GetGame()
        {
            return game;
        }

        public void Warn(string message)
        {
            if (!BaseTebexAdapter.PluginConfig.SuppressWarnings)
            {
                LogWarning("{0}", message);    
            }
        }

        public void Error(string message)
        {
            if (!BaseTebexAdapter.PluginConfig.SuppressErrors)
            {
                LogError("{0}", message);    
            }
        }

        public void Info(string info)
        {
            Puts("{0}", info);
        }

        private void OnUserConnected(IPlayer player)
        {
            // Check for default config and inform the admin that configuration is waiting.
            if (player.IsAdmin && BaseTebexAdapter.PluginConfig.SecretKey == "your-secret-key-here")
            {
                player.Command("chat.add", 0, player.Id,
                    "Tebex is not configured. Use tebex:secret <secret> from the F1 menu to add your key.");
                player.Command("chat.add", 0, player.Id, "Get your secret key by logging in at:");
                player.Command("chat.add", 0, player.Id, "https://tebex.io/");
            }

            _adapter.LogDebug($"Player login event: {player.Id}@{player.Address}");
            _adapter.OnUserConnected(player.Id, player.Address);
        }

        #if RUST // VIP notes are enabled on Rust only
        void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (!BaseTebexAdapter.PluginConfig.VipNotesEnabled)
            {
                return;
            }
            
            // If the user configured no VIP codes, skip.
            if (BaseTebexAdapter.PluginConfig.VipCodes.Count == 0)
            {
                return;
            }

            // Check if we have a target loot crate by prefab name
            string prefabName = entity?.ShortPrefabName;
            if (string.IsNullOrEmpty(prefabName) || (prefabName != "crate_normal_2" &&
                                                     prefabName != "crate_normal_2_food" &&
                                                     prefabName != "crate_normal_2_tools"))
            {
                return;
            }

            // Ensure the player was provided
            string userID = player?.UserIDString;
            if (string.IsNullOrEmpty(userID))
            {
                return;
            }

            // If the player is already in a VIP group, they won't receive a VIP note
            if (BaseTebexAdapter.PluginConfig.VipGroups.Any(group => permission.UserHasGroup(userID, group)))
            {
                return;
            }

            // Make sure we haven't spawned a note too recently for the user.
            if (_lastNoteGiven.TryGetValue(userID, out DateTime lastGivenTime) &&
                (DateTime.Now - lastGivenTime).Seconds < BaseTebexAdapter.PluginConfig.NoteCooldown)
            {
                return;
            }

            // Spawn chance check
            if (Oxide.Core.Random.Range(0.0f, 1.0f) > BaseTebexAdapter.PluginConfig.NoteSpawnChance)
            {
                return;
            }

            // Create the note
            Item item = ItemManager.CreateByName("note", 1, 0UL);
            if (item == null)
            {
                return;
            }

            List<string> messages = BaseTebexAdapter.PluginConfig.NoteMessages["en"];
            string message = messages[Oxide.Core.Random.Range(0, messages.Count)];
            string vipCode = BaseTebexAdapter.PluginConfig.VipCodes[Oxide.Core.Random.Range(0, BaseTebexAdapter.PluginConfig.VipCodes.Count)];

            var info = BaseTebexAdapter.Cache.Instance.Get("information").Value as TebexApi.TebexStoreInfo;
            if (info != null)
            {
                item.text = string.Format(message, player.displayName, info.AccountInfo.Domain, vipCode);
                item.MarkDirty();
                player.GiveItem(item, BaseEntity.GiveItemReason.Generic);

                _lastNoteGiven[userID] = DateTime.Now;                
            }
            else
            {
                _adapter.LogDebug("Store information not present in cache when trying to spawn VIP note!");
            }
        }
        #endif
        
        private void OnServerShutdown()
        {
            // Make sure join queue is always emptied on shutdown
            _adapter.ProcessJoinQueue();
        }

        private void PrintCategories(IPlayer player, List<TebexApi.Category> categories)
        {
            // Index counter for selecting displayed items
            var categoryIndex = 1;
            var packIndex = 1;

            // Line separator for category response
            _adapter.ReplyPlayer(player, "---------------------------------");

            // Sort categories in order and display
            var orderedCategories = categories.OrderBy(category => category.Order).ToList();
            for (int i = 0; i < categories.Count; i++)
            {
                var listing = orderedCategories[i];
                _adapter.ReplyPlayer(player, $"[C{categoryIndex}] {listing.Name}");
                categoryIndex++;

                // Show packages for the category in order from API
                if (listing.Packages.Count > 0)
                {
                    var packages = listing.Packages.OrderBy(category => category.Order).ToList();
                    _adapter.ReplyPlayer(player, $"Packages");
                    foreach (var package in packages)
                    {
                        // Add additional flair on sales
                        if (package.Sale != null && package.Sale.Active)
                        {
                            _adapter.ReplyPlayer(player,
                                $"-> [P{packIndex}] {package.Name} {package.Price - package.Sale.Discount} (SALE {package.Sale.Discount} off)");
                        }
                        else
                        {
                            _adapter.ReplyPlayer(player, $"-> [P{packIndex}] {package.Name} {package.Price}");
                        }

                        packIndex++;
                    }
                }

                // At the end of each category add a line separator
                _adapter.ReplyPlayer(player, "---------------------------------");
            }
        }

        private static void PrintPackages(IPlayer player, List<TebexApi.Package> packages)
        {
            // Index counter for selecting displayed items
            var packIndex = 1;

            _adapter.ReplyPlayer(player, "---------------------------------");
            _adapter.ReplyPlayer(player, "      PACKAGES AVAILABLE         ");
            _adapter.ReplyPlayer(player, "---------------------------------");

            // Sort categories in order and display
            var orderedPackages = packages.OrderBy(package => package.Order).ToList();
            for (var i = 0; i < packages.Count; i++)
            {
                var package = orderedPackages[i];
                // Add additional flair on sales
                _adapter.ReplyPlayer(player, $"[P{packIndex}] {package.Name}");
                _adapter.ReplyPlayer(player, $"Category: {package.Category.Name}");
                _adapter.ReplyPlayer(player, $"Description: {package.Description}");

                if (package.Sale != null && package.Sale.Active)
                {
                    _adapter.ReplyPlayer(player,
                        $"Original Price: {package.Price} {package.GetFriendlyPayFrequency()}  SALE: {package.Sale.Discount} OFF!");
                }
                else
                {
                    _adapter.ReplyPlayer(player, $"Price: {package.Price} {package.GetFriendlyPayFrequency()}");
                }

                _adapter.ReplyPlayer(player,
                    $"Purchase with 'tebex.checkout P{packIndex}' or 'tebex.checkout {package.Id}'");
                _adapter.ReplyPlayer(player, "--------------------------------");

                packIndex++;
            }
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(GetDefaultConfig(), true);
        }

        private BaseTebexAdapter.TebexConfig GetDefaultConfig()
        {
            return new BaseTebexAdapter.TebexConfig();
        }

        [Command("tebex.secret", "tebex:secret")]
        private void TebexSecretCommand(IPlayer player, string command, string[] args)
        {
            // Secret can only be ran as the admin
            if (!player.HasPermission("tebexplugin.secret"))
            {
                _adapter.ReplyPlayer(player, "You do not have permission to run this command.");
                _adapter.ReplyPlayer(player, "If you are an admin, grant permission to use `tebex.secret`");
                return;
            }

            if (args.Length != 1)
            {
                _adapter.ReplyPlayer(player, "Invalid syntax. Usage: \"tebex.secret <secret>\"");
                return;
            }

            _adapter.ReplyPlayer(player, "Setting your secret key...");
            BaseTebexAdapter.PluginConfig.SecretKey = args[0];
            Config.WriteObject(BaseTebexAdapter.PluginConfig);

            // Reset store info so that we don't fetch from the cache
            BaseTebexAdapter.Cache.Instance.Remove("information");

            // Any failure to set secret key is logged to console automatically
            _adapter.FetchStoreInfo(info =>
            {
                _adapter.ReplyPlayer(player, $"Successfully set your secret key.");
                _adapter.ReplyPlayer(player,
                    $"Store set as: {info.ServerInfo.Name} for the web store {info.AccountInfo.Name}");

                PluginEvent.SERVER_ID = info.ServerInfo.Id.ToString();
                PluginEvent.STORE_URL = info.AccountInfo.Domain;
                _adapter.SetSecretKeyValidated(true);
            });
        }

        [Command("tebex.info", "tebex:info", "tebex.information", "tebex:information")]
        private void TebexInfoCommand(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("tebexplugin.info"))
            {
                _adapter.ReplyPlayer(player, "You do not have permission to run that command.");
                return;
            }

            _adapter.ReplyPlayer(player, "Getting store information...");
            _adapter.FetchStoreInfo(info =>
            {
                _adapter.ReplyPlayer(player, "Information for this server:");
                _adapter.ReplyPlayer(player, $"{info.ServerInfo.Name} for webstore {info.AccountInfo.Name}");
                _adapter.ReplyPlayer(player, $"Server prices are in {info.AccountInfo.Currency.Iso4217}");
                _adapter.ReplyPlayer(player, $"Webstore domain {info.AccountInfo.Domain}");
            });
        }

        [Command("tebex.checkout", "tebex:checkout")]
        private void TebexCheckoutCommand(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("tebexplugin.checkout"))
            {
                _adapter.ReplyPlayer(player, "You do not have permission to run that command.");
                return;
            }

            if (player.IsServer)
            {
                _adapter.ReplyPlayer(player,
                    $"{command} cannot be executed via server. Use tebex:sendlink <username> <packageId> to specify a target player.");
                return;
            }

            // Only argument will be the package ID of the item in question
            if (args.Length != 1)
            {
                _adapter.ReplyPlayer(player, "Invalid syntax: Usage \"tebex.checkout <packageId>\"");
                return;
            }

            // Lookup the package by provided input and respond with the checkout URL
            var package = _adapter.GetPackageByShortCodeOrId(args[0].Trim());
            if (package == null)
            {
                _adapter.ReplyPlayer(player, "A package with that ID was not found.");
                return;
            }

            _adapter.ReplyPlayer(player, "Creating your checkout URL...");
            _adapter.CreateCheckoutUrl(player.Name, package, checkoutUrl =>
            {
                player.Command("chat.add", 0, player.Id, "Please visit the following URL to complete your purchase:");
                player.Command("chat.add", 0, player.Id, $"{checkoutUrl.Url}");
            }, error => { _adapter.ReplyPlayer(player, $"{error.ErrorMessage}"); });
        }

        [Command("tebex.help", "tebex:help")]
        private void TebexHelpCommand(IPlayer player, string command, string[] args)
        {
            _adapter.ReplyPlayer(player, "Tebex Commands Available:");
            if (player.IsAdmin) //Always show help to admins regardless of perms, for new server owners
            {
                _adapter.ReplyPlayer(player, "-- Administrator Commands --");
                _adapter.ReplyPlayer(player, "tebex.secret <secretKey>          - Sets your server's secret key.");
                _adapter.ReplyPlayer(player, "tebex.debug <on/off>              - Enables or disables debug logging.");
                _adapter.ReplyPlayer(player,
                    "tebex.sendlink <player> <packId>  - Sends a purchase link to the provided player.");
                _adapter.ReplyPlayer(player,
                    "tebex.forcecheck                  - Forces the command queue to check for any pending purchases.");
                _adapter.ReplyPlayer(player,
                    "tebex.refresh                     - Refreshes store information, packages, categories, etc.");
                _adapter.ReplyPlayer(player,
                    "tebex.report                      - Generates a report for the Tebex support team.");
                _adapter.ReplyPlayer(player,
                    "tebex.ban <playerId>              - Bans a player from using your Tebex store.");
                _adapter.ReplyPlayer(player,
                    "tebex.lookup <playerId>           - Looks up store statistics for the given player.");
            }

            _adapter.ReplyPlayer(player, "-- User Commands --");
            _adapter.ReplyPlayer(player,
                "tebex.info                       - Get information about this server's store.");
            _adapter.ReplyPlayer(player,
                "tebex.categories                 - Shows all item categories available on the store.");
            _adapter.ReplyPlayer(player,
                "tebex.packages <opt:categoryId>  - Shows all item packages available in the store or provided category.");
            _adapter.ReplyPlayer(player,
                "tebex.checkout <packId>          - Creates a checkout link for an item. Visit to purchase.");
        }
        
        [Command("tebex.debug", "tebex:debug")]
        private void TebexDebugCommand(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("tebexplugin.debug"))
            {
                _adapter.ReplyPlayer(player, "You do not have permission to run that command.");
                return;
            }

            if (args.Length != 1)
            {
                _adapter.ReplyPlayer(player, "Usage: tebex.debug <on/off>");
                return;
            }

            if (args[0].Equals("on"))
            {
                BaseTebexAdapter.PluginConfig.DebugMode = true;
                Config.WriteObject(BaseTebexAdapter.PluginConfig);
                _adapter.ReplyPlayer(player, "Debug mode is enabled.");
            }
            else if (args[0].Equals("off"))
            {
                BaseTebexAdapter.PluginConfig.DebugMode = false;
                Config.WriteObject(BaseTebexAdapter.PluginConfig);
                _adapter.ReplyPlayer(player, "Debug mode is disabled.");
            }
            else
            {
                _adapter.ReplyPlayer(player, "Usage: tebex.debug <on/off>");
            }
        }

        [Command("tebex.forcecheck", "tebex:forcecheck")]
        private void TebexForceCheckCommand(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("tebexplugin.forcecheck"))
            {
                _adapter.ReplyPlayer(player, "You do not have permission to run that command.");
                return;
            }

            _adapter.RefreshStoreInformation(true);
            _adapter.ProcessCommandQueue(true);
            _adapter.ProcessJoinQueue(true);
            _adapter.DeleteExecutedCommands(true);
        }

        [Command("tebex.refresh", "tebex:refresh")]
        private void TebexRefreshCommand(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("tebexplugin.refresh"))
            {
                _adapter.ReplyPlayer(player, "You do not have permission to run that command.");
                return;
            }

            _adapter.ReplyPlayer(player, "Refreshing listings...");
            BaseTebexAdapter.Cache.Instance.Remove("packages");
            BaseTebexAdapter.Cache.Instance.Remove("categories");

            _adapter.RefreshListings((code, body) =>
            {
                if (BaseTebexAdapter.Cache.Instance.HasValid("packages") &&
                    BaseTebexAdapter.Cache.Instance.HasValid("categories"))
                {
                    var packs = (List<TebexApi.Package>)BaseTebexAdapter.Cache.Instance.Get("packages").Value;
                    var categories = (List<TebexApi.Category>)BaseTebexAdapter.Cache.Instance.Get("categories").Value;
                    _adapter.ReplyPlayer(player,
                        $"Fetched {packs.Count} packages out of {categories.Count} categories");
                }
            });
        }

        [Command("tebex.ban", "tebex:ban")]
        private void TebexBanCommand(IPlayer commandRunner, string command, string[] args)
        {
            if (!commandRunner.HasPermission("tebexplugin.ban"))
            {
                _adapter.ReplyPlayer(commandRunner, $"{command} can only be used by administrators.");
                return;
            }

            if (args.Length < 2)
            {
                _adapter.ReplyPlayer(commandRunner, $"Usage: tebex.ban <playerName> <reason>");
                return;
            }

            var player = players.FindPlayer(args[0].Trim());
            if (player == null)
            {
                _adapter.ReplyPlayer(commandRunner, $"Could not find that player on the server.");
                return;
            }

            var reason = string.Join(" ", args.Skip(1));
            _adapter.ReplyPlayer(commandRunner, $"Processing ban for player {player.Name} with reason '{reason}'");
            _adapter.BanPlayer(player.Name, player.Address, reason,
                (code, body) => { _adapter.ReplyPlayer(commandRunner, "Player banned successfully."); },
                error => { _adapter.ReplyPlayer(commandRunner, $"Could not ban player. {error.ErrorMessage}"); });
        }

        [Command("tebex.unban", "tebex:unban")]
        private void TebexUnbanCommand(IPlayer commandRunner, string command, string[] args)
        {
            if (!commandRunner.IsAdmin)
            {
                _adapter.ReplyPlayer(commandRunner, $"{command} can only be used by administrators.");
                return;
            }

            _adapter.ReplyPlayer(commandRunner, $"You must unban players via your webstore.");
        }

        [Command("tebex.categories", "tebex:categories", "tebex.listings", "tebex:listings")]
        private void TebexCategoriesCommand(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("tebexplugin.categories"))
            {
                _adapter.ReplyPlayer(player, "You do not have permission to run that command.");
                return;
            }

            _adapter.GetCategories(categories => { PrintCategories(player, categories); });
        }

        [Command("tebex.packages", "tebex:packages")]
        private void TebexPackagesCommand(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("tebexplugin.packages"))
            {
                _adapter.ReplyPlayer(player, "You do not have permission to run that command.");
                return;
            }

            _adapter.GetPackages(packages => { PrintPackages(player, packages); });
        }

        [Command("tebex.lookup", "tebex:lookup")]
        private void TebexLookupCommand(IPlayer player, string command, string[] args)
        {
            if (!player.HasPermission("tebexplugin.lookup"))
            {
                _adapter.ReplyPlayer(player, "You do not have permission to run that command.");
                return;
            }

            if (args.Length != 1)
            {
                _adapter.ReplyPlayer(player, $"Usage: tebex.lookup <playerId/playerUsername>");
                return;
            }

            // Try to find the given player
            var target = players.FindPlayer(args[0]);
            if (target == null)
            {
                _adapter.ReplyPlayer(player, $"Could not find a player matching the name or id {args[0]}.");
                return;
            }

            _adapter.GetUser(target.Id, (code, body) =>
            {
                var response = JsonConvert.DeserializeObject<TebexApi.UserInfoResponse>(body);
                _adapter.ReplyPlayer(player, $"Username: {response.Player.Username}");
                _adapter.ReplyPlayer(player, $"Id: {response.Player.Id}");
                _adapter.ReplyPlayer(player, $"Payments Total: ${response.Payments.Sum(payment => payment.Price)}");
                _adapter.ReplyPlayer(player, $"Chargeback Rate: {response.ChargebackRate}%");
                _adapter.ReplyPlayer(player, $"Bans Total: {response.BanCount}");
                _adapter.ReplyPlayer(player, $"Payments: {response.Payments.Count}");
            }, error => { _adapter.ReplyPlayer(player, error.ErrorMessage); });
        }

        [Command("tebex.sendlink", "tebex:sendlink")]
        private void TebexSendLinkCommand(IPlayer commandRunner, string command, string[] args)
        {
            if (!commandRunner.HasPermission("tebexplugin.sendlink"))
            {
                _adapter.ReplyPlayer(commandRunner, "You must be an administrator to run this command.");
                return;
            }

            if (args.Length != 2)
            {
                _adapter.ReplyPlayer(commandRunner, "Usage: tebex.sendlink <username> <packageId>");
                return;
            }

            var username = args[0].Trim();
            var package = _adapter.GetPackageByShortCodeOrId(args[1].Trim());
            if (package == null)
            {
                _adapter.ReplyPlayer(commandRunner, "A package with that ID was not found.");
                return;
            }

            _adapter.ReplyPlayer(commandRunner,
                $"Creating checkout URL with package '{package.Name}'|{package.Id} for player {username}");
            var player = players.FindPlayer(username);
            if (player == null)
            {
                _adapter.ReplyPlayer(commandRunner, $"Couldn't find that player on the server.");
                return;
            }

            _adapter.CreateCheckoutUrl(player.Name, package, checkoutUrl =>
            {
                player.Command("chat.add", 0, player.Id, "Please visit the following URL to complete your purchase:");
                player.Command("chat.add", 0, player.Id, $"{checkoutUrl.Url}");
            }, error => { _adapter.ReplyPlayer(player, $"{error.ErrorMessage}"); });
        }
        
        
    }
}