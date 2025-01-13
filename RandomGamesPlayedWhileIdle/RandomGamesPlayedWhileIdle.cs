using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AngleSharp.Dom;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Integration;
using ArchiSteamFarm.Web.Responses;
using SteamKit2;

namespace LeastPlayedGamesWhileIdle {
    [Export(typeof(IPlugin))]
    public sealed partial class LeastPlayedGamesWhileIdlePlugin : IBotConnection {
        private const int MaxGamesPlayedConcurrently = 32;

        public string Name => nameof(LeastPlayedGamesWhileIdlePlugin);
        public Version Version => typeof(LeastPlayedGamesWhileIdlePlugin).Assembly.GetName().Version!;

        public Task OnLoaded() => Task.CompletedTask;
        public Task OnBotDisconnected(Bot bot, EResult reason) => Task.CompletedTask;

        public async Task OnBotLoggedOn(Bot bot) {
            ArgumentNullException.ThrowIfNull(bot);

            try {
                // Attempt to fetch your Steam profile's /games page
                using HtmlDocumentResponse? response = await bot.ArchiWebHandler
                    .UrlGetToHtmlDocumentWithSession(new Uri(ArchiWebHandler.SteamCommunityURL, $"profiles/{bot.SteamID}/games"))
                    .ConfigureAwait(false);

                // Check if we successfully got the HTML and found the element
                if (response?.Content?.SelectSingleNode("""//*[@id="gameslist_config"]""") is Element element) {
                    string outerHtml = element.OuterHtml;
                    ASF.ArchiLogger.LogGenericInfo($"[LeastPlayedGames] Retrieved HTML snippet length: {outerHtml.Length}");

                    // Use a regex to find every { "appid":..., "playtime_forever":... } pair
                    var matches = GamesListRegex().Matches(outerHtml);

                    ASF.ArchiLogger.LogGenericInfo($"[LeastPlayedGames] Found {matches.Count} matches for appid/playtime.");

                    // If no matches, we can bail or just log a warning
                    if (matches.Count == 0) {
                        ASF.ArchiLogger.LogGenericWarning("[LeastPlayedGames] No appid/playtime pairs found in HTML, skipping.");
                        return;
                    }

                    // Convert matches into a list of (AppID, Playtime)
                    List<(uint AppID, uint PlaytimeMinutes)> allGames = new();
                    foreach (Match m in matches) {
                        try {
                            uint appId = uint.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                            // Steam reports playtime_forever in total minutes
                            uint playtime = uint.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

                            allGames.Add((appId, playtime));
                        } catch (Exception ex) {
                            // Just log if parsing fails for some reason
                            ASF.ArchiLogger.LogGenericError($"[LeastPlayedGames] Error parsing appid or playtime: {ex.Message}");
                        }
                    }

                    ASF.ArchiLogger.LogGenericInfo($"[LeastPlayedGames] Successfully parsed {allGames.Count} game entries.");

                    // Sort by ascending played time
                    List<(uint AppID, uint PlaytimeMinutes)> leastPlayed = allGames
                        .OrderBy(x => x.PlaytimeMinutes)
                        .Take(MaxGamesPlayedConcurrently)
                        .ToList();

                    // Debug: show the top few
                    ASF.ArchiLogger.LogGenericInfo("[LeastPlayedGames] Top picks: " +
                        string.Join(", ", leastPlayed.Select(x => $"(AppID={x.AppID},Minutes={x.PlaytimeMinutes})"))
                    );

                    // Set them as the "GamesPlayedWhileIdle"
                    if (leastPlayed.Count > 0) {
                        bot.BotConfig.GetType().GetProperty("GamesPlayedWhileIdle")?.SetValue(
                            bot.BotConfig,
                            leastPlayed.Select(x => x.AppID).ToImmutableList()
                        );

                        ASF.ArchiLogger.LogGenericInfo($"[LeastPlayedGames] Assigned {leastPlayed.Count} least-played games to idle.");
                    } else {
                        ASF.ArchiLogger.LogGenericInfo("[LeastPlayedGames] No games found to idle!");
                    }
                } else {
                    ASF.ArchiLogger.LogGenericWarning("[LeastPlayedGames] Did not find element with id='gameslist_config' in the HTML.");
                }
            } catch (Exception e) {
                ASF.ArchiLogger.LogGenericException(e);
            }
        }

        // This regex tries to capture lines that look like:
        // {&quot;appid&quot;:1234, ... &quot;playtime_forever&quot;:5678 ...
        // If your actual format is slightly different, adjust the pattern below.
        // Note: This uses C# 10+ "source generators" for regex; if that's not supported, remove the [GeneratedRegex] attribute
        // and define a static readonly Regex with new Regex("pattern", RegexOptions.Compiled).
        [GeneratedRegex("\\{&quot;appid&quot;:(\\d+),.*?&quot;playtime_forever&quot;:(\\d+)", RegexOptions.Singleline)]
        private static partial Regex GamesListRegex();
    }
}
