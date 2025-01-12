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

namespace RandomGamesPlayedWhileIdle {
	[Export(typeof(IPlugin))]
	public sealed partial class RandomGamesPlayedWhileIdlePlugin : IBotConnection {
		private const int MaxGamesPlayedConcurrently = 32;

		public string Name => nameof(RandomGamesPlayedWhileIdle);
		public Version Version => typeof(RandomGamesPlayedWhileIdlePlugin).Assembly.GetName().Version!;

		public Task OnLoaded() => Task.CompletedTask;
		public Task OnBotDisconnected(Bot bot, EResult reason) => Task.CompletedTask;

		public async Task OnBotLoggedOn(Bot bot) {
			ArgumentNullException.ThrowIfNull(bot);

			try {
				using HtmlDocumentResponse? response = await bot.ArchiWebHandler
					.UrlGetToHtmlDocumentWithSession(new Uri(ArchiWebHandler.SteamCommunityURL,
						$"profiles/{bot.SteamID}/games")).ConfigureAwait(false);

				if (response?.Content?.SelectSingleNode("""//*[@id="gameslist_config"]""") is Element element) {
					ASF.ArchiLogger.LogGenericInfo("Retrieved games data: " + new string(element.OuterHtml.AsSpan(0, Math.Min(element.OuterHtml.Length, 500))));

					try {
						var matches = GamesListRegex().Matches(element.OuterHtml);

						var gameList = matches.Select(match => new {
							AppID = uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
							PlaytimeMinutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) // Assuming playtime is in minutes
						})
						.OrderBy(game => game.PlaytimeMinutes) // Sort by playtime (ascending)
						.Take(MaxGamesPlayedConcurrently) // Take the N least played games
						.ToList();

						ASF.ArchiLogger.LogGenericInfo("Selected games and playtimes: " + string.Join(", ", gameList.Select(game => $"AppID: {game.AppID}, Playtime: {game.PlaytimeMinutes} minutes")));

						if (gameList.Count > 0) {
							bot.BotConfig.GetType().GetProperty("GamesPlayedWhileIdle")?.SetValue(bot.BotConfig, gameList.Select(game => game.AppID).ToImmutableList());
						}
					} catch (Exception ex) {
						ASF.ArchiLogger.LogGenericError("Error while parsing game data: " + ex.Message);
						ASF.ArchiLogger.LogGenericError("Raw data for debugging: " + element.OuterHtml);
					}
				}
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}

		[GeneratedRegex(@"{&quot;appid&quot;:(\d+),&quot;playtime_forever&quot;:(\d+),&quot;name&quot;:&quot;")]
		private static partial Regex GamesListRegex();
	}
}
