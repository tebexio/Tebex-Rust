using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Tebex.API;
using Tebex.Triage;

namespace Tebex.Adapters
{
    public abstract class BaseTebexAdapter
    {
        public static BaseTebexAdapter Instance => _adapterInstance.Value;
        private static readonly Lazy<BaseTebexAdapter> _adapterInstance = new Lazy<BaseTebexAdapter>();
        
        public static TebexConfig PluginConfig { get; set; } = new TebexConfig();
        
        /** For rate limiting command queue based on next_check */
        private static DateTime _nextCheck = DateTime.Now;

        private static List<TebexApi.TebexJoinEventInfo> _eventQueue = new List<TebexApi.TebexJoinEventInfo>();
        
        /** For storing successfully executed commands and deleting them from API */
        protected static readonly List<TebexApi.Command> ExecutedCommands = new List<TebexApi.Command>();

        /** Allow pausing all web requests if rate limits are received from remote */
        protected bool IsRateLimited = false;
        
        public abstract void Init();

        public void DeleteExecutedCommands()
        {
            LogDebug("Checking for completed commands...");
            if (ExecutedCommands.Count == 0)
            {
                LogDebug("No commands to flush.");
                return;
            }

            LogDebug($"Found {ExecutedCommands.Count} commands to flush.");

            List<int> ids = new List<int>();
            foreach (var command in ExecutedCommands)
            {
                ids.Add(command.Id);
            }

            TebexApi.Instance.DeleteCommands(ids.ToArray(), (code, body) =>
            {
                LogInfo("Successfully flushed completed commands.");
                ExecutedCommands.Clear();
            }, (error) =>
            {
                LogError($"Failed to flush completed commands: {error.ErrorMessage}");
            }, (code, body) =>
            {
                LogError($"Unexpected error while flushing completed commands. API response code {code}. Response body follows:");
                LogError(body);
            });
        }

        /**
         * Logs a warning to the console and game log.
         */
        public abstract void LogWarning(string message);

        /**
         * Logs an error to the console and game log.
         */
        public abstract void LogError(string message);

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
            if (_eventQueue.Count > 10) //TODO make configurable?
            {
                ProcessJoinQueue();
            }
        }
        
        public class TebexConfig
        {
            // Enables additional debug logging, which may show raw user info in console.
            public bool DebugMode = false;

            // Automatically sends detected issues to Tebex 
            public bool AutoReportingEnabled = true;
            
            //public bool AllowGui = false;
            public string SecretKey = "your-secret-key-here";
            public int CacheLifetime = 30;
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
                        LogError("Failed to parse fetched store information.");
                        LogError(body);
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
                    LogError("Could not get refresh all listings! Response body from API follows:");
                    LogError(body);
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
                    LogError("Could not get refresh package listings! Response body from API follows:");
                    LogError(body);
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
            catch (Exception e) //FIXME reports of very infuriating NPE in this func
            {
                ReportAutoTriageEvent(TebexTriage.CreateAutoTriageEvent("Raised exception while refreshing packages", new Dictionary<string, string>
                {
                    {"cacheHasValid", Cache.Instance.HasValid("packages").ToString()},
                    {"error", e.Message},
                    {"trace", e.StackTrace}
                }));
                LogError("An error occurred while getting your store's packages. This has been reported automatically.");
            }
        }

        // Periodically keeps store info updated from the API
        public void RefreshStoreInformation()
        {
            // Calling places the information in the cache
            FetchStoreInfo((info => { })); //NOOP
        }
        
        public void ProcessJoinQueue()
        {
            if (_eventQueue.Count > 0)
            {
                LogDebug("Processing join queue...");
                TebexApi.Instance.PlayerJoinEvent(_eventQueue, (code, body) =>
                    {
                        LogDebug("Join queue cleared successfully.");
                        _eventQueue.Clear();
                    }, error =>
                    {
                        ReportAutoTriageEvent(TebexTriage.CreateAutoTriageEvent("API error while processing join queue", new Dictionary<string, string>()
                        {
                            {"error",error.ErrorMessage},
                        }));
                        LogError($"Could not process join queue - error response from API: {error.ErrorMessage}");
                    },
                    (code, body) =>
                    {
                        ReportAutoTriageEvent(TebexTriage.CreateAutoTriageEvent("Server error while processing join queue", new Dictionary<string, string>()
                        {
                            {"code",code.ToString()},
                            {"responseBody",body},
                        }));
                        LogError("Could not process join queue - unexpected server error.");
                        LogError(body);
                    });
            }
        }
        
        public bool CanProcessNextCommandQueue()
        {
            return DateTime.Now > _nextCheck;
        }

        public void ProcessCommandQueue()
        {
            if (!CanProcessNextCommandQueue())
            {
                var secondsToWait = (int)(_nextCheck - DateTime.Now).TotalSeconds;
                LogError($"Tried to run command queue, but should wait another {secondsToWait} seconds.");
                return;
            }

            // Get the state of the command queue
            TebexApi.Instance.GetCommandQueue((cmdQueueCode, cmdQueueResponseBody) =>
            {
                var response = JsonConvert.DeserializeObject<TebexApi.CommandQueueResponse>(cmdQueueResponseBody);
                if (response == null)
                {
                    LogError("Failed to get command queue. Could not parse response from API. Response body follows:");
                    LogError(cmdQueueResponseBody);
                    return;
                }

                // Set next available check time
                _nextCheck = DateTime.Now.AddSeconds(response.Meta.NextCheck);

                // Process offline commands immediately
                if (response.Meta != null && response.Meta.ExecuteOffline)
                {
                    LogInfo("Requesting offline commands from API...");
                    TebexApi.Instance.GetOfflineCommands((code, offlineCommandsBody) =>
                    {
                        var offlineCommands = JsonConvert.DeserializeObject<TebexApi.OfflineCommandsResponse>(offlineCommandsBody);
                        if (offlineCommands == null)
                        {
                            ReportAutoTriageEvent(TebexTriage.CreateAutoTriageEvent("Failed to parse offline commands response body", new Dictionary<string, string>()
                            {
                                {"code", code.ToString()},
                                {"responseBody", offlineCommandsBody}
                            }));
                            LogError("Failed to get offline commands. Could not parse response from API. Response body follows:");
                            LogError(offlineCommandsBody);
                            return;
                        }

                        LogInfo($"Found {offlineCommands.Commands.Count} offline commands to execute.");
                        foreach (TebexApi.Command command in offlineCommands.Commands)
                        {
                            var parsedCommand = ExpandOfflineVariables(command.CommandToRun, command.Player);
                            var splitCommand = parsedCommand.Split(' ');
                            var commandName = splitCommand[0];
                            var args = splitCommand.Skip(1);
                            
                            LogInfo($"Executing offline command: `{parsedCommand}`");
                            ExecuteOfflineCommand(command, commandName, args.ToArray());
                        }
                    }, (error) =>
                    {
                        ReportAutoTriageEvent(TebexTriage.CreateAutoTriageEvent("Error response from API while processing offline commands", new Dictionary<string, string>()
                        {
                            {"error",error.ErrorMessage},
                            {"errorCode", error.ErrorCode.ToString()}
                        }));
                        LogError($"Error response from API while processing offline commands: {error.ErrorMessage}");
                    }, (offlineComandsCode, offlineCommandsServerError) =>
                    {
                        ReportAutoTriageEvent(TebexTriage.CreateAutoTriageEvent("Server error from API while processing offline commands", new Dictionary<string, string>()
                        {
                            {"code", offlineComandsCode.ToString()},
                            {"responseBody", offlineCommandsServerError}
                        }));
                        LogError("Unexpected error response from API while processing offline commands");
                        LogError(offlineCommandsServerError);
                    });
                }
                else
                {
                    LogInfo("No offline commands to execute.");
                }

                // Process any online commands 
                LogInfo($"Found {response.Players.Count} due players in the queue");
                foreach (var duePlayer in response.Players)
                {
                    LogInfo($"Processing online commands for player {duePlayer.Name}...");
                    if (!IsPlayerOnline(duePlayer.UUID))
                    {
                        LogInfo($"Player {duePlayer.Name} has online commands but is not connected. Skipping.");
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
                                ReportAutoTriageEvent(TebexTriage.CreateAutoTriageEvent("Could not parse response from API while processing online commands of player", new Dictionary<string, string>()
                                {
                                    {"playerName", duePlayer.Name},
                                    {"code", onlineCommandsCode.ToString()},
                                    {"responseBody", onlineCommandsResponseBody}
                                }));
                                
                                LogError($"> Failed to get online commands for ${duePlayer.Name}. Could not unmarshal response from API.");
                                return;
                            }

                            LogInfo($"> Processing {onlineCommands.Commands.Count} commands for this player...");
                            foreach (var command in onlineCommands.Commands)
                            {
                                object playerRef = GetPlayerRef(onlineCommands.Player.Id);
                                if (playerRef == null)
                                {
                                    LogError($"No reference found for expected online player. Commands will be skipped for this player.");
                                    break;
                                }

                                var parsedCommand = ExpandUsernameVariables(command.CommandToRun, playerRef);
                                var splitCommand = parsedCommand.Split(' ');
                                var commandName = splitCommand[0];
                                var args = splitCommand.Skip(1);
                                
                                LogDebug($"Pre-execution: {parsedCommand}");
                                ExecuteOnlineCommand(command, playerRef, commandName, args.ToArray());
                                LogDebug($"Post-execution: {parsedCommand}");
                                ExecutedCommands.Add(command);
                            }
                        }, tebexError => // Error for this player's online commands
                        {
                            ReportAutoTriageEvent(TebexTriage.CreateAutoTriageEvent("API responded with error while processing online player's commands", new Dictionary<string, string>()
                            {
                                {"playerName", duePlayer.Name},
                                {"code", tebexError.ErrorCode.ToString()},
                                {"message", tebexError.ErrorMessage}
                            }));
                            
                            LogError("Failed to get due online commands due to error response from API.");
                            LogError(tebexError.ErrorMessage);
                        });
                }
            }, tebexError => // Error for get due players
            {
                ReportAutoTriageEvent(TebexTriage.CreateAutoTriageEvent("API responded with error while getting due players", new Dictionary<string, string>()
                {
                    {"code", tebexError.ErrorCode.ToString()},
                    {"message", tebexError.ErrorMessage}
                }));
                LogError("Failed to get due players due to error response from API.");
                LogError(tebexError.ErrorMessage);
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

        public void BanPlayer(string playerName, string playerIp, String reason, TebexApi.ApiSuccessCallback onSuccess,
            TebexApi.ApiErrorCallback onError)
        {
            TebexApi.Instance.CreateBan(reason, playerIp, playerName, onSuccess, onError);
        }

        public void GetUser(string userId, TebexApi.ApiSuccessCallback onSuccess = null,
            TebexApi.ApiErrorCallback onApiError = null, TebexApi.ServerErrorCallback onServerError = null)
        {
            TebexApi.Instance.GetUser(userId, onSuccess, onApiError, onServerError);
        }
        
        /**
         * Sends a message to the given player.
         */
        public abstract void ReplyPlayer(object player, string message);

        public abstract void ExecuteOfflineCommand(TebexApi.Command command, string commandName, string[] args);
        public abstract void ExecuteOnlineCommand(TebexApi.Command command, object playerObj, string commandName, string[] args);
        
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

        public abstract TebexTriage.AutoTriageEvent FillAutoTriageParameters(TebexTriage.AutoTriageEvent partialEvent);
        
        public void ReportAutoTriageEvent(TebexTriage.AutoTriageEvent autoTriageEvent)
        {
            if (!PluginConfig.AutoReportingEnabled)
            {
                return;
            }
            
            // Determine store name
            // Determine the store info, if we have it.
            var storeName = "";
            var storeUrl = "";
            
            if (Cache.Instance.HasValid("information"))
            {
                TebexApi.TebexStoreInfo storeInfo = (TebexApi.TebexStoreInfo)Cache.Instance.Get("information").Value;
                storeName = storeInfo.AccountInfo.Name;
                storeUrl = storeInfo.AccountInfo.Domain;
            }

            autoTriageEvent.StoreName = storeName;
            autoTriageEvent.StoreUrl = storeUrl;
            
            // Fill missing params using the framework adapter
            autoTriageEvent = FillAutoTriageParameters(autoTriageEvent);
            
            MakeWebRequest(TebexApi.TebexTriageUrl, JsonConvert.SerializeObject(autoTriageEvent),
                TebexApi.HttpVerb.POST,
                (code, body) =>
                {
                    LogDebug("Successfully submitted auto triage event");
                }, (error) =>
                {
                    LogDebug("Triage API responded with error: " + error.ErrorMessage);
                }, (code, body) =>
                {
                    LogDebug("Triage API encountered a server error while submitting triage event: " + body);
                });
        }

        public void ReportManualTriageEvent(TebexTriage.ReportedTriageEvent reportedTriageEvent, TebexApi.ApiSuccessCallback onSuccess, TebexApi.ServerErrorCallback onError)
        {
            var storeName = "";
            var storeUrl = "";
            
            if (Cache.Instance.HasValid("information"))
            {
                TebexApi.TebexStoreInfo storeInfo = (TebexApi.TebexStoreInfo)Cache.Instance.Get("information").Value;
                storeName = storeInfo.AccountInfo.Name;
                storeUrl = storeInfo.AccountInfo.Domain;
            }

            reportedTriageEvent.StoreName = storeName;
            reportedTriageEvent.StoreUrl = storeUrl;
            
            MakeWebRequest(TebexApi.TebexTriageUrl, JsonConvert.SerializeObject(reportedTriageEvent),
                TebexApi.HttpVerb.POST,
                (code, body) =>
                {
                    LogDebug("Successfully submitted manual triage event");
                    onSuccess(code, body);
                }, (error) =>
                {
                    LogDebug("Triage API responded with error: " + error.ErrorMessage);
                }, (code, body) =>
                {
                    LogDebug("Triage API encountered a server error while submitting manual triage event: " + body);
                    onError(code, body);
                });
        }
    }
}