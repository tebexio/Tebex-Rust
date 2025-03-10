using Newtonsoft.Json;
using Oxide.Core.Plugins;
using Tebex.API;

namespace Tebex.Adapters
{
    public abstract class BaseTebexAdapter
    {
        public static BaseTebexAdapter Instance => _adapterInstance.Value;
        private static readonly Lazy<BaseTebexAdapter> _adapterInstance = new Lazy<BaseTebexAdapter>();
        
        public static TebexConfig PluginConfig { get; set; } = new TebexConfig();

        private static bool _isSecretKeyValidated = false;
        
        /** For rate limiting command queue based on next_check */
        private static DateTime _nextCheckCommandQueue = DateTime.Now;
        
        // Time checks for our plugin timers.
        private static DateTime _nextCheckDeleteCommands = DateTime.Now;
        private static DateTime _nextCheckJoinQueue = DateTime.Now;
        private static DateTime _nextCheckRefresh = DateTime.Now;
        
        private static List<TebexApi.TebexJoinEventInfo> _eventQueue = new List<TebexApi.TebexJoinEventInfo>();
        
        /** For storing successfully executed commands and deleting them from API */
        protected static readonly List<TebexApi.Command> ExecutedCommands = new List<TebexApi.Command>();

        /** Allow pausing all web requests if rate limits are received from remote */
        protected bool IsRateLimited = false;
        
        public abstract void Init();

        public void DeleteExecutedCommands(bool ignoreWaitCheck = false)
        {
            LogDebug("Deleting executed commands...");
            if (!IsSecretKeyValidated())
            {
                LogDebug("Store key is not set or incorrect. Skipping command queue.");
                return;
            }
            
            if (!CanProcessNextDeleteCommands() && !ignoreWaitCheck)
            {
                LogDebug("Skipping check for completed commands - not time to be processed");
                return;
            }
            
            if (ExecutedCommands.Count == 0)
            {
                LogDebug("  No commands to flush.");
                return;
            }

            LogDebug($"  Found {ExecutedCommands.Count} commands to flush.");

            List<int> ids = new List<int>();
            foreach (var command in ExecutedCommands)
            {
                ids.Add(command.Id);
            }

            _nextCheckDeleteCommands = DateTime.Now.AddSeconds(60);
            TebexApi.Instance.DeleteCommands(ids.ToArray(), (code, body) =>
            {
                LogDebug("Successfully flushed completed commands.");
                ExecutedCommands.Clear();
            }, (error) =>
            {
                LogDebug($"Failed to flush completed commands: {error.ErrorMessage}");
            }, (code, body) =>
            {
                LogDebug($"Unexpected error while flushing completed commands. API response code {code}. Response body follows:");
                LogDebug(body);
            });
        }

        /**
         * Logs a warning to the console and game log.
         */
        public abstract void LogWarning(string message, string solution);

        public abstract void LogWarning(string message, string solution, Dictionary<String, String> metadata);

        /**
         * Logs an error to the console and game log.
         */
        public abstract void LogError(string message);

        public abstract void LogError(string message, Dictionary<String, String> metadata);
        
        /**
             * Logs information to the console and game log.
             */
        public abstract void LogInfo(string message);

        /**
             * Logs debug information to the console and game log if debug mode is enabled.
             */
        public abstract void LogDebug(string message);

        public void OnUserConnected(string steam64Id, string ip)
        {
            var joinEvent = new TebexApi.TebexJoinEventInfo(steam64Id, "server.join", DateTime.Now, ip);
            _eventQueue.Add(joinEvent);

            // If we're already over a threshold, go ahead and send the events.
            if (_eventQueue.Count > 10)
            {
                ProcessJoinQueue();
            }
        }
        
        public class TebexConfig
        {
            // Enables additional debug logging, which may show raw user info in console.
            public bool DebugMode = false;

            public bool SuppressWarnings = false;

            public bool SuppressErrors = false;
            
            // Automatically sends detected issues to Tebex 
            public bool AutoReportingEnabled = true;
            
            //public bool AllowGui = false;
            public string SecretKey = "your-secret-key-here";
            public int CacheLifetime = 30;
            
            //#if RUST
            [JsonProperty(PropertyName = "VIP Notes Enabled")]
            public bool VipNotesEnabled { get; set; } = false;
            
            [JsonProperty(PropertyName = "VIP Codes")]
            public List<string> VipCodes { get; set; } = new List<string>();
            
            [JsonProperty(PropertyName = "VIP Groups")]
            public List<string> VipGroups { get; set; } = new List<string>();

            [JsonProperty(PropertyName = "Note Spawn Chance")]
            public float NoteSpawnChance { get; set; } = 0.02f; // 2%
            
            [JsonProperty(PropertyName = "Note Cooldown (Seconds)")]
            public float NoteCooldown { get; set; } = 600; // 10 minutes

            [JsonProperty(PropertyName = "Note Messages")]
            public Dictionary<string, List<string>> NoteMessages { get; set; } = new Dictionary<string, List<string>>
            {
                {
                    "en", new List<string>
                    {
                        "Hey {0}, grab your exclusive 10% OFF VIP offer with code {2} at {1}! Limited time only!",
                        "{0}, seize your special discount! Use code {2} at {1} for a limited time offer!",
                        "Special surprise for you, {0}! Use code {2} at {1} for 10% off and enjoy your VIP benefits!"
                    }
                },
            };
            
            //#endif
        }
        
        public class Cache
        {
            public static Cache Instance => _cacheInstance.Value;
            private static readonly Lazy<Cache> _cacheInstance = new Lazy<Cache>(() => new Cache());
            private static Dictionary<string, CachedObject> _cache = new Dictionary<string, CachedObject>();
            public CachedObject Get(string key)
            {
                if (_cache.ContainsKey(key))
                {
                    return _cache[key];
                }
                return null;
            }

            public void Set(string key, CachedObject obj)
            {
                _cache[key] = obj;
            }

            public bool HasValid(string key)
            {
                return _cache.ContainsKey(key) && !_cache[key].HasExpired();
            }

            public void Clear()
            {
                _cache.Clear();
            }

            public void Remove(string key)
            {
                _cache.Remove(key);
            }
        }

        public class CachedObject
        {
            public object Value { get; private set; }
            private DateTime _expires;

            public CachedObject(object obj, int minutesValid)
            {
                Value = obj;
                _expires = DateTime.Now.AddMinutes(minutesValid);
            }

            public bool HasExpired()
            {
                return DateTime.Now > _expires;
            }
        }
        
        /** Callback type to use /information response */
        public delegate void FetchStoreInfoResponse(TebexApi.TebexStoreInfo info);

        /**
             * Returns the store's /information payload. Info is cached according to configured cache lifetime.
             */
        public void FetchStoreInfo(FetchStoreInfoResponse response)
        {
            if (Cache.Instance.HasValid("information"))
            {
                response?.Invoke((TebexApi.TebexStoreInfo)Cache.Instance.Get("information").Value);
            }
            else
            {
                TebexApi.Instance.Information((code, body) =>
                {
                    var storeInfo = JsonConvert.DeserializeObject<TebexApi.TebexStoreInfo>(body);
                    if (storeInfo == null)
                    {
                        LogError("Failed to parse fetched store information!", new Dictionary<string, string>()
                        {
                            {"response", body},
                        });
                        return;
                    }

                    Cache.Instance.Set("information", new CachedObject(storeInfo, PluginConfig.CacheLifetime));
                    response?.Invoke(storeInfo);
                });
            }
        }

        /** Callback type for response from creating checkout url */
        public delegate void CreateCheckoutUrlResponse(TebexApi.CheckoutUrlPayload checkoutUrl);

        public TebexApi.Package GetPackageByShortCodeOrId(string value)
        {
            var shortCodes = (Dictionary<String, TebexApi.Package>)Cache.Instance.Get("packageShortCodes").Value;
            if (shortCodes.ContainsKey(value))
            {
                return shortCodes[value];
            }

            // No short code found, assume it's a package ID
            var packages = (List<TebexApi.Package>)Cache.Instance.Get("packages").Value;
            foreach (var package in packages)
            {
                if (package.Id.ToString() == value)
                {
                    return package;
                }
            }

            // Package not found
            return null;
        }

        /**
             * Refreshes cached categories and packages from the Tebex API. Can be used by commands or with no arguments
             * to update the information while the server is idle.
             */
        public void RefreshListings(TebexApi.ApiSuccessCallback onSuccess = null)
        {
            // Get our categories from the /listing endpoint as it contains all category data
            TebexApi.Instance.GetListing((code, body) =>
            {
                var response = JsonConvert.DeserializeObject<TebexApi.ListingsResponse>(body);
                if (response == null)
                {
                    LogError("Could not get refresh all listings!", new Dictionary<string, string>()
                    {
                        {"response", body},
                    });
                    return;
                }

                Cache.Instance.Set("categories", new CachedObject(response.categories, PluginConfig.CacheLifetime));
                if (onSuccess != null)
                {
                    onSuccess.Invoke(code, body);    
                }
            });

            // Get our packages from a verbose get all packages call so that we always have the description
            // of the package cached.
            TebexApi.Instance.GetAllPackages(true, (code, body) =>
            {
                var response = JsonConvert.DeserializeObject<List<TebexApi.Package>>(body);
                if (response == null)
                {
                    LogError("Could not refresh package listings!", new Dictionary<string, string>()
                    {
                        {"response", body}
                    });
                    return;
                }

                Cache.Instance.Set("packages", new CachedObject(response, PluginConfig.CacheLifetime));

                // Generate and save shortcodes for each package
                var orderedPackages = response.OrderBy(package => package.Order).ToList();
                var shortCodes = new Dictionary<String, TebexApi.Package>();
                for (var i = 0; i < orderedPackages.Count; i++)
                {
                    var package = orderedPackages[i];
                    shortCodes.Add($"P{i + 1}", package);
                }

                Cache.Instance.Set("packageShortCodes", new CachedObject(shortCodes, PluginConfig.CacheLifetime));
                onSuccess?.Invoke(code, body);
            });
        }

        /** Callback type for getting all categories */
        public delegate void GetCategoriesResponse(List<TebexApi.Category> categories);

        /**
             * Gets all categories and their packages (no description) from the API. Response is cached according to the
             * configured cache lifetime.
             */
        public void GetCategories(GetCategoriesResponse onSuccess,
            TebexApi.ServerErrorCallback onServerError = null)
        {
            if (Cache.Instance.HasValid("categories"))
            {
                onSuccess.Invoke((List<TebexApi.Category>)Cache.Instance.Get("categories").Value);
            }
            else
            {
                TebexApi.Instance.GetListing((code, body) =>
                {
                    var response = JsonConvert.DeserializeObject<TebexApi.ListingsResponse>(body);
                    if (response == null)
                    {
                        onServerError?.Invoke(code, body);
                        return;
                    }

                    Cache.Instance.Set("categories", new CachedObject(response.categories, PluginConfig.CacheLifetime));
                    onSuccess.Invoke(response.categories);
                });
            }
        }

        /** Callback type for working with packages received from the API */
        public delegate void GetPackagesResponse(List<TebexApi.Package> packages);

        /** Gets all package info from API. Response is cached according to the configured cache lifetime. */
        public void GetPackages(GetPackagesResponse onSuccess,
            TebexApi.ServerErrorCallback onServerError = null)
        {
            try
            {
                if (Cache.Instance.HasValid("packages"))
                {
                    onSuccess.Invoke((List<TebexApi.Package>)Cache.Instance.Get("packages").Value);
                }
                else
                {
                    // Updates both packages and shortcodes in the cache
                    RefreshListings((code, body) =>
                    {
                        onSuccess.Invoke((List<TebexApi.Package>)Cache.Instance.Get("packages").Value);
                    });
                }
            }
            catch (Exception e)
            {
                LogError("An error occurred while getting your store's packages. " + e.Message, new Dictionary<string, string>()
                {
                    {"trace", e.StackTrace},
                    {"message", e.Message}
                });
            }
        }

        // Periodically keeps store info updated from the API
        public void RefreshStoreInformation(bool ignoreWaitCheck = false)
        {
            LogDebug("Refreshing store information...");
            if (!IsSecretKeyValidated())
            {
                LogDebug("Store key is not set or incorrect. Skipping command queue.");
                return;
            }
            
            // Calling places the information in the cache
            if (!CanProcessNextRefresh() && !ignoreWaitCheck)
            {
                LogDebug("  Skipping store info refresh - not time to be processed");
                return;
            }
            
            _nextCheckRefresh = DateTime.Now.AddMinutes(15);
            FetchStoreInfo(info =>
            {
            }); // automatically stores in the cache
        }
        
        public void ProcessJoinQueue(bool ignoreWaitCheck = false)
        {
            LogDebug("Processing player join queue...");
            if (!IsSecretKeyValidated())
            {
                LogDebug("Store key is not set or incorrect. Skipping command queue.");
                return;
            }
            
            if (!CanProcessNextJoinQueue() && !ignoreWaitCheck)
            {
                LogDebug("  Skipping join queue - not time to be processed");
                return;
            }
            
            _nextCheckJoinQueue = DateTime.Now.AddSeconds(60);
            if (_eventQueue.Count > 0)
            {
                LogDebug($"  Found {_eventQueue.Count} join events.");
                TebexApi.Instance.PlayerJoinEvent(_eventQueue, (code, body) =>
                    {
                        LogDebug("Join queue cleared successfully.");
                        _eventQueue.Clear();
                    }, error =>
                    {
                        LogError($"Could not process join queue - error response from API: {error.ErrorMessage}");
                    },
                    (code, body) =>
                    {
                        LogError("Could not process join queue - unexpected server error.", new Dictionary<string, string>()
                        {
                            {"response", body},
                            {"code", code.ToString()},
                        });
                    });
            }
            else // Empty queue
            {
                LogDebug($"  No recent join events.");
            }
        }
        
        public bool CanProcessNextCommandQueue()
        {
            return DateTime.Now > _nextCheckCommandQueue;
        }

        public bool CanProcessNextDeleteCommands()
        {
            return DateTime.Now > _nextCheckDeleteCommands;
        }
        
        public bool CanProcessNextJoinQueue()
        {
            return DateTime.Now > _nextCheckJoinQueue;
        }
        
        public bool CanProcessNextRefresh()
        {
            return DateTime.Now > _nextCheckRefresh;
        }
        
        public void ProcessCommandQueue(bool ignoreWaitCheck = false)
        {
            LogDebug("Processing command queue...");
            if (!IsSecretKeyValidated())
            {
                LogDebug("Store key is not set or incorrect. Skipping command queue.");
                return;
            }
            
            if (!CanProcessNextCommandQueue() && !ignoreWaitCheck)
            {
                var secondsToWait = (int)(_nextCheckCommandQueue - DateTime.Now).TotalSeconds;
                LogDebug($"  Tried to run command queue, but should wait another {secondsToWait} seconds.");
                return;
            }

            // Get the state of the command queue
            TebexApi.Instance.GetCommandQueue((cmdQueueCode, cmdQueueResponseBody) =>
            {
                var response = JsonConvert.DeserializeObject<TebexApi.CommandQueueResponse>(cmdQueueResponseBody);
                if (response == null)
                {
                    LogError("Failed to get command queue. Could not parse response from API.", new Dictionary<string, string>()
                    {
                        {"response", cmdQueueResponseBody},
                        {"code", cmdQueueCode.ToString()},
                    });
                    return;
                }

                // Set next available check time
                _nextCheckCommandQueue = DateTime.Now.AddSeconds(response.Meta.NextCheck);

                // Process offline commands immediately
                if (response.Meta != null && response.Meta.ExecuteOffline)
                {
                    LogDebug("Requesting offline commands from API...");
                    TebexApi.Instance.GetOfflineCommands((code, offlineCommandsBody) =>
                    {
                        var offlineCommands = JsonConvert.DeserializeObject<TebexApi.OfflineCommandsResponse>(offlineCommandsBody);
                        if (offlineCommands == null)
                        {
                            LogError("Failed to get offline commands. Could not parse response from API.", new Dictionary<string, string>()
                            {
                                {"code", code.ToString()},
                                {"responseBody", offlineCommandsBody}
                            });
                            return;
                        }

                        LogDebug($"Found {offlineCommands.Commands.Count} offline commands to execute.");
                        foreach (TebexApi.Command command in offlineCommands.Commands)
                        {
                            var parsedCommand = ExpandOfflineVariables(command.CommandToRun, command.Player);
                            var splitCommand = parsedCommand.Split(' ');
                            var commandName = splitCommand[0];
                            var args = splitCommand.Skip(1);
                            
                            LogDebug($"Executing offline command: `{parsedCommand}`");
                            ExecuteOfflineCommand(command, null, commandName, args.ToArray());
                            ExecutedCommands.Add(command);
                            LogDebug($"Executed commands queue has {ExecutedCommands.Count} commands");
                        }
                    }, (error) =>
                    {
                        LogError($"Error response from API while processing offline commands: {error.ErrorMessage}", new Dictionary<string, string>()
                        {
                            {"error",error.ErrorMessage},
                            {"errorCode", error.ErrorCode.ToString()}
                        });
                    }, (offlineComandsCode, offlineCommandsServerError) =>
                    {
                        LogError("Unexpected error response from API while processing offline commands", new Dictionary<string, string>()
                        {
                            {"code", offlineComandsCode.ToString()},
                            {"responseBody", offlineCommandsServerError}
                        });
                    });
                }
                else
                {
                    LogDebug("No offline commands to execute.");
                }

                // Process any online commands 
                LogDebug($"Found {response.Players.Count} due players in the queue");
                foreach (var duePlayer in response.Players)
                {
                    LogDebug($"Processing online commands for player {duePlayer.Name}...");
                    object playerRef = GetPlayerRef(duePlayer.UUID);
                    if (playerRef == null)
                    {
                        LogDebug($"> Player {duePlayer.Name} has online commands but is no ref found (are they connected?) Skipping.");
                        continue;
                    }
                    
                    TebexApi.Instance.GetOnlineCommands(duePlayer.Id,
                        (onlineCommandsCode, onlineCommandsResponseBody) =>
                        {
                            LogDebug(onlineCommandsResponseBody);
                            var onlineCommands =
                                JsonConvert.DeserializeObject<TebexApi.OnlineCommandsResponse>(
                                    onlineCommandsResponseBody);
                            if (onlineCommands == null)
                            { 
                                LogError($"> Failed to get online commands for ${duePlayer.Name}. Could not unmarshal response from API.", new Dictionary<string, string>()
                                {
                                    {"playerName", duePlayer.Name},
                                    {"code", onlineCommandsCode.ToString()},
                                    {"responseBody", onlineCommandsResponseBody}
                                });
                                return;
                            }

                            LogDebug($"> Processing {onlineCommands.Commands.Count} commands for this player...");
                            foreach (var command in onlineCommands.Commands)
                            {
                                var parsedCommand = ExpandUsernameVariables(command.CommandToRun, playerRef);
                                var splitCommand = parsedCommand.Split(' ');
                                var commandName = splitCommand[0];
                                var args = splitCommand.Skip(1);
                                
                                LogDebug($"Pre-execution: {parsedCommand}");
                                var success = ExecuteOnlineCommand(command, playerRef, commandName, args.ToArray());
                                LogDebug($"Post-execution: {parsedCommand}");
                                if (success)
                                {
                                    ExecutedCommands.Add(command);    
                                }
                            }
                        }, tebexError => // Error for this player's online commands
                        {
                            LogError("Failed to get due online commands due to error response from API.", new Dictionary<string, string>()
                            {
                                {"playerName", duePlayer.Name},
                                {"code", tebexError.ErrorCode.ToString()},
                                {"message", tebexError.ErrorMessage}
                            });
                        });
                }
            }, tebexError => // Error for get due players
            {
                LogError("Failed to get due players due to error response from API.", new Dictionary<string, string>()
                {
                    {"code", tebexError.ErrorCode.ToString()},
                    {"message", tebexError.ErrorMessage}
                });
            });
        }

        /**
     * Creates a checkout URL for a player to purchase the given package.
     */
        public void CreateCheckoutUrl(string playerName, TebexApi.Package package,
            CreateCheckoutUrlResponse success,
            TebexApi.ApiErrorCallback error)
        {
            TebexApi.Instance.CreateCheckoutUrl(package.Id, playerName, (code, body) =>
            {
                var responsePayload = JsonConvert.DeserializeObject<TebexApi.CheckoutUrlPayload>(body);
                if (responsePayload == null)
                {
                    return;
                }

                success?.Invoke(responsePayload);
            }, error);
        }

        public delegate void GetGiftCardsResponse(List<TebexApi.GiftCard> giftCards);

        public delegate void GetGiftCardByIdResponse(TebexApi.GiftCard giftCards);

        public void GetGiftCards(GetGiftCardsResponse success, TebexApi.ApiErrorCallback error)
        {
            //TODO
        }

        public void GetGiftCardById(GetGiftCardByIdResponse success, TebexApi.ApiErrorCallback error)
        {
            //TODO
        }

        public void BanPlayer(string playerName, string playerIp, string reason, TebexApi.ApiSuccessCallback onSuccess,
            TebexApi.ApiErrorCallback onError)
        {
            TebexApi.Instance.CreateBan(reason, playerIp, playerName, onSuccess, onError);
        }

        public void GetUser(string userId, TebexApi.ApiSuccessCallback onSuccess = null,
            TebexApi.ApiErrorCallback onApiError = null, TebexApi.ServerErrorCallback onServerError = null)
        {
            TebexApi.Instance.GetUser(userId, onSuccess, onApiError, onServerError);
        }

        public void GetActivePackagesForCustomer(string playerId, int? packageId = null, TebexApi.ApiSuccessCallback onSuccess = null,
            TebexApi.ApiErrorCallback onApiError = null, TebexApi.ServerErrorCallback onServerError = null)
        {
            TebexApi.Instance.GetActivePackagesForCustomer(playerId, packageId, onSuccess, onApiError, onServerError);
        }
        
        /**
         * Sends a message to the given player.
         */
        public abstract void ReplyPlayer(object player, string message);

        public abstract void ExecuteOfflineCommand(TebexApi.Command command, object playerObj, string commandName, string[] args);
        public abstract bool ExecuteOnlineCommand(TebexApi.Command command, object playerObj, string commandName, string[] args);
        
        public abstract bool IsPlayerOnline(string playerRefId);
        public abstract object GetPlayerRef(string playerId);

        /**
         * As we support the use of different games across the Tebex Store
         * we offer slightly different ways of getting a customer username or their ID.
         * 
         * All games support the same default variables, but some games may have additional variables.
         */
        public abstract string ExpandUsernameVariables(string input, object playerObj);

        public abstract string ExpandOfflineVariables(string input, TebexApi.PlayerInfo info);
        
        public abstract void MakeWebRequest(string endpoint, string body, TebexApi.HttpVerb verb,
            TebexApi.ApiSuccessCallback onSuccess, TebexApi.ApiErrorCallback onApiError,
            TebexApi.ServerErrorCallback onServerError);

        public bool IsSecretKeyValidated()
        {
            return _isSecretKeyValidated;
        }
        
        public void SetSecretKeyValidated(bool value)
        {
            _isSecretKeyValidated = value;
        }
    }
}