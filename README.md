# Cinephage Requests (Jellyfin-plugin)

Voegt op de Jellyfin-zoekpagina, direct onder de zoekbalk, een sectie **"Aanvragen via Cinephage"** toe. Gebruikers kunnen daar films en series aanvragen. Ze hebben **geen Cinephage-account** nodig.

- Elke kaart heeft een label **Film** of **Serie**.
- Bij een serie opent **Seizoenen kiezen** een venster met alle seizoenen en hun status. Seizoenen die al beschikbaar zijn, al aangevraagd zijn of nog geen afleveringen hebben, zijn niet te kiezen. Specials (seizoen 0) staan er niet bij, omdat Cinephage die niet als verzoek accepteert.
- Verzoeken gaan met de API-key van Cinephage naar Cinephage. Ze worden daar **direct goedgekeurd**, omdat de key bij het admin-account hoort.
- In Cinephage staat bij elk verzoek de admin als aanvrager. Wie het echt was, houdt de plugin zelf bij. Je ziet dat in Jellyfin onder **Dashboard → Cinephage-verzoeken**: datum, Jellyfin-gebruiker, titel (met seizoenen bij series) en de actuele Cinephage-status.
- Vraagt iemand iets aan dat al aangevraagd is, dan komt dat ook in het overzicht ("Was al aangevraagd"). Zo zie je dat meer mensen het wilden.
- Cinephage voegt de titel bij een verzoek meteen toe aan de bibliotheek, en dat kan lang duren. De plugin wacht daarom maximaal 2 minuten. Komt er dan nog geen antwoord, dan kijkt hij in Cinephage of het verzoek toch is aangekomen.

## Vereisten

- Jellyfin **12.0.x** (gebouwd tegen 12.0.0, `targetAbi 12.0.0.0`)
- Cinephage met het verzoeksysteem (*Requests*-pagina). Dit zit op dit moment alleen in de **dev**-builds (`ghcr.io/moldytaint/cinephage:dev`), niet in release v0.17.0.
- Een TMDB-key ingesteld in Cinephage
- De **Main API-key** van Cinephage (Instellingen → Systeem → API keys). De streaming-key werkt niet.

## Installeren via repository

1. Ga in Jellyfin naar Dashboard → Plugins → **Repositories** (Catalogus → tandwiel) en voeg deze repository toe:
   ```
   https://raw.githubusercontent.com/ddylvianen/jellyfin-plugin-cinephage/main/manifest.json
   ```
2. Installeer **Cinephage Requests** uit de catalogus en herstart Jellyfin.
3. Ga naar Dashboard → Plugins → **Cinephage Requests**. Vul de Cinephage-URL en de API-key in, sla op en klik op **Verbinding testen**.
4. Ververs de Jellyfin-webinterface (Ctrl+F5) en zoek een film.

## Bouwen

Je hebt alleen Docker nodig (de build draait in de .NET 10 SDK-container):

```bash
./build.sh
# -> dist/cinephage-requests_<versie>.zip, plus versie en MD5-checksum
```

## Nieuwe versie uitbrengen

1. Verhoog `AssemblyVersion` en `FileVersion` in `Jellyfin.Plugin.Cinephage/Jellyfin.Plugin.Cinephage.csproj`.
2. Draai `./build.sh`.
3. Maak een release met de zip: `gh release create v<versie> dist/cinephage-requests_<versie>.zip`.
4. Zet bovenaan in `versions` in `manifest.json` een nieuw item met `version`, `sourceUrl`, `checksum` (uit `build.sh`) en `timestamp`. Commit en push. Jellyfin ziet de update dan vanzelf.

Het overzicht van wie wat heeft aangevraagd staat in `<config>/plugins/Jellyfin.Plugin.Cinephage/requests.json`.

## Endpoints (Jellyfin)

| Methode | Pad | Auth |
|---|---|---|
| GET | `/Cinephage/Search?query=` | ingelogde gebruiker |
| GET | `/Cinephage/Series/{tmdbId}/Seasons` | ingelogde gebruiker |
| POST | `/Cinephage/Request` `{ "MediaType": "movie"\|"series", "TmdbId": 123, "Seasons": [1, 2] }` | ingelogde gebruiker |
| GET | `/Cinephage/Requests` | Jellyfin-admin |
| POST | `/Cinephage/Test` | Jellyfin-admin |
| GET | `/Cinephage/Script` | anoniem (het script) |

## Bekende beperkingen

- Series kun je per seizoen aanvragen, niet per losse aflevering.
- Elke Jellyfin-gebruiker kan aanvragen, en de downloads starten direct. Een limiet per gebruiker is er niet.
- De zoekpagina-integratie hangt af van de DOM van jellyfin-web 12 (`#searchPage`, `.searchResults`). Een update van jellyfin-web kan die breken.
