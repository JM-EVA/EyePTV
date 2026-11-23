# EyePTV voor Windows 11

Een eigen IPTV-kijkapp met een **persoonlijke zenderlijst**.

## Hoe het werkt
1. **Bron toevoegen** (*Beheer → Bronnen*): een M3U-link of een Xtream Codes-account (server + gebruikersnaam + wachtwoord; een geplakte `get.php?username=…`-link wordt herkend).
2. **Eerste keer: kiezen.** Je ziet de volledige lijst van je provider, gegroepeerd, met zoeken en "alles selecteren" per groep of zoekopdracht. Heeft de bron ook films en series, dan staan die op eigen tabbladen (*Zenders / Films / Series*) met eigen groepen en tellers. Alleen wat je aanvinkt komt in *Mijn zenders*.
3. **Opslaan.** Je lijst, favorieten en volgorde staan in `%APPDATA%\IptvPlayer`.
4. **Bij een update** (automatisch bij het opstarten als de laatste sync meer dan 6 uur geleden is, of via *Bijwerken*) verschijnt bovenin een melding. In het review-scherm zie je **alleen wat nieuw is** (zenders, films en series, elk op een eigen tabblad); wat je niet aanvinkt wordt overgeslagen en komt niet meer terug. Items uit je lijst die de provider niet meer heeft, kun je daar in één keer opruimen.
5. Gewijzigde links of namen van zenders die al in je lijst staan worden stil bijgewerkt, zodat tokens in URL's je lijst niet breken. Je favorieten en volgorde blijven behouden.
6. Later nog eens zenders, films of series toevoegen of weghalen? *Beheer → Bronnen → Inhoud beheren* toont de volledige lijst met je huidige keuze aangevinkt.

Verder: favorieten (ster), volgorde aanpassen (rechtsklik), TV-gids (nu/straks + programmalijst per dag, alleen voor zenders in je lijst), volledig scherm met F11.

## Films en series
- Alleen bij **Xtream Codes** komen films en series als aparte soorten binnen. Bij een **M3U-lijst** worden links met `/movie/` of een videobestand (.mkv, .mp4, …) als film herkend; een serie bestaat daar niet als geheel.
- **Films**: dubbelklik om te kijken. De app onthoudt waar je was (vanaf 30 seconden; bijna uit = *bekeken*) en gaat de volgende keer **verder op die plek**. Pauze en spoelen met de knoppen, de balk of het toetsenbord (spatie = pauze, ← 15 s terug, → 15 s vooruit).
- **Series**: dubbelklik op een serie opent de seizoenen en afleveringen (pas dan worden ze bij de provider opgehaald). Je begint bij het seizoen waar je gebleven bent; na een aflevering start automatisch de volgende. Vorige/volgende-knoppen gaan door de afleveringen.
- Je kijkvoortgang staat in `watch.json` (maximaal 500 items).

## Downloaden
- Bij een **film** staat in de lijst een knop **⬇** (ook in het rechtsklikmenu); bij een **serie** open je eerst de afleveringen en klik je op ⬇ bij een aflevering, of op **⬇ Seizoen** om het hele seizoen te downloaden. Alleen items met een http(s)-link (Xtream, of een M3U-film) kunnen.
- Een download komt **zoals de provider hem aanbiedt** op je schijf (meestal `.mkv` of `.mp4`, niet omgezet), standaard in `Video's\EyePTV\Downloads`. De map kies je bij **Beheer → Downloads**, waar je ook de voortgang ziet, kunt **pauzeren, hervatten, afspelen en verwijderen**.
- Er wordt **één tegelijk** gedownload (veel providers staan weinig verbindingen toe). Valt de verbinding weg, of sluit je de app, dan gaat de download bij de volgende keer **verder waar het bestand was**.
- Een gedownloade film of aflevering speelt de app voortaan **van je schijf** (dus ook zonder internet), met dezelfde kijkvoortgang als bij streamen.

## Reservezenders
Rechtsklik op een live zender → **Reservezender instellen…** en vink aan welke zenders uit je lijst het overnemen (zenders met dezelfde naam staan bovenaan). Geeft de zender een fout of start hij niet binnen het aantal seconden dat je bij *Beheer → Voorkeuren* instelt (standaard 10), dan schakelt de app naar de eerste reserve en meldt dat onderin. Elke reserve komt per poging één keer aan bod; werkt niets, dan stopt de app met een melding. Kies je zelf weer een zender, dan begin je opnieuw bij de hoofdzender.

## Casten naar een tv
- Onder de video staat een klein **castpictogram** zodra er iets speelt. Een klik zoekt apparaten in je netwerk en toont ze in een menu; kies je tv (**Chromecast of Google TV**) en wat je kijkt gaat verder op de tv, op dezelfde plek. *Weer op deze pc kijken* brengt het terug. Het volume regel je met de afstandsbediening van de tv.
- De pc en de tv moeten op **hetzelfde netwerk** zitten. Windows vraagt de eerste keer of EyePTV het netwerk mag gebruiken: kies *Privénetwerken*.
- De pc doet het werk (de stream gaat via de pc naar de tv). Bij een zwakke pc of een zwakke wifi-verbinding kan het hakkelen.
- **4K:** de meeste tv's en een gewone Chromecast spelen geen 4K/HEVC. Staat dezelfde zender er ook in HD, dan kiest de app bij het casten vanzelf die versie. Een 4K-film heeft zo'n alternatief niet.

## Beginscherm
- Het **huisje** linksboven in het menu brengt je altijd terug naar het beginscherm. EyePTV start op een **beginscherm** in de stijl van Apple TV. Bovenaan staat wat je het laatst bekeek met een grote knop **Hervatten**; daaronder grote tegels naar *Kanalen*, *Films*, *Series* en de *Gids*.
- **Verder kijken**: films en afleveringen die je half hebt bekeken (per serie de laatste aflevering), met een voortgangsbalk. Een klik start op de plek waar je was. Daarna volgen je favoriete kanalen met wat er nu op is en je opnames.
- **Beeld van het kanaal:** de kaarten met kanalen tonen een momentopname van de uitzending in plaats van het logo (dat staat klein in de hoek). De app opent de kanalen daarvoor een paar seconden, één voor één, en alleen als er niets speelt of wordt opgenomen; beelden worden 20 minuten hergebruikt. Uitzetten kan bij *Beheer → Voorkeuren*.
- **Verwijderen of markeren:** wijs een kaart aan en klik op de puntjes (of rechtsklik) voor *Markeren als bekeken* en *Verwijderen uit Verder kijken*; bij de grote kaart bovenaan staat dezelfde knop naast *Hervatten*.
- **Erotiek** (groepen als *Adults* of *XXX*, titels met `[X]`) is overal verborgen (lijsten, zoeken, beginscherm, gids, opnames en downloads) tot je het ontgrendelt met een **code van 4 cijfers** (standaard `0000`; ontgrendelen en wijzigen bij *Beheer → Voorkeuren*). De app is bij elke start vergrendeld.

## Gids en opnemen
- Het tabblad **Gids** is een **rooster**: zenders onder elkaar, de tijd van links naar rechts, een lijn bij *nu*. Schuif zijwaarts met de balk onderaan of met Shift + muiswiel; bij zenders met terugkijken ga je ook de afgelopen dagen terug. Filter op groep of favorieten. Klik op een programma voor de details en de acties: **Kijk nu** (loopt nu), **↺ Terugkijken** (afgelopen), **🔔 Herinner mij** (komt nog) en **Opnemen**.
- **Zoeken in de gids:** het zoekveld bovenaan doorzoekt titel en omschrijving van alle programma's van je zenders (hoofdletters en accenten maken niet uit, minstens 2 tekens); de resultaten staan op starttijd en hebben dezelfde acties.
- **Herinneringen:** een bel bij een programma betekent dat je er een melding voor krijgt, standaard 5 minuten van tevoren (*Beheer → Voorkeuren → Herinneringen*). De melding verschijnt bovenin de app met *Nu kijken* en *Sluiten*, en het taakbalkicoon knippert. Dit werkt alleen terwijl de app open is; bij het opstarten krijg je de herinneringen waarvan het programma nog loopt. *Mijn herinneringen* (rechtsboven in de Gids) toont de lijst en laat je ze weghalen.
- **Opnemen** kan ook met de knop *Opnemen* in de speler bij een zender: dit programma, 30 minuten, 1 of 2 uur, of **tot je zelf stopt**. Een opname van een programma begint **2 minuten eerder** en loopt **5 minuten langer** door (instelbaar bij Instellingen), en de eindtijd **volgt de EPG** als het programma uitloopt.
- De stream wordt rechtstreeks naar een `.ts`-bestand geschreven (standaard `Video's\EyePTV`) en na afloop zonder kwaliteitsverlies omgezet naar `.mp4` (lukt dat niet, dan blijft de `.ts` staan). De opname loopt door als de verbinding even wegvalt. **De app moet openstaan** om op te nemen; veel providers staan maar 1 of 2 gelijktijdige streams toe.
- Beheer van opnames (stoppen, annuleren, afspelen, verwijderen, map en marges) staat bij **Beheer → Opnames**. Sluit je de app terwijl er een opname loopt of gepland staat, dan vraagt EyePTV of je zeker weet dat je wilt afsluiten of het venster liever alleen minimaliseert (dan blijft het opnemen doorgaan).

## Audio en ondertitels
- Bij een film of aflevering scant de app de **audiosporen** en **ondertitels** in de video. Boven de bediening staan keuzelijsten (alleen als er iets te kiezen is) en een overzicht, bijvoorbeeld *2 audiosporen · 3 ondertitels*.
- Bij **Beheer → Voorkeuren** kies je je **voorkeurstaal voor ondertiteling** en voor **audio**. Heeft de video een ondertitel in jouw taal, dan zet de app die automatisch aan (uit te zetten). Ontbreekt die, dan staat er een hint. Ondertitels downloaden of online zoeken kan niet.

## Terugkijken (catch-up)
- Zenders die terugkijken aanbieden hebben een **↺** in de lijst. In de programmagids kies je een dag (vandaag, gisteren, …) en klik je bij een programma op **↺** om het **vanaf het begin** te kijken; met *Terug naar live* ga je weer naar de uitzending.
- Hoeveel dagen terug je kunt, bepaalt de provider (Xtream: `tv_archive_duration`; M3U: `catchup-days`/`tvg-rec`). Bij zenders met archief haalt de app tot 14 dagen aan programmagegevens op.
- Ondersteund: Xtream Codes (de gebruikelijke `/timeshift/`-link) en M3U-lijsten met `catchup="shift"`, `"append"`, `"default"` of `"xc"`. Andere varianten (bijv. Flussonic) worden niet herkend; die zenders krijgen geen terugkijkknop.
- De starttijd in de link wordt omgerekend naar de tijdzone van de provider (uit het Xtream-account). Staat die er niet in, dan wordt UTC gebruikt; loopt het terugkijken dan een uur of meer ernaast, dan is dat de eerste plek om te kijken.
- Spoelen tijdens terugkijken kan altijd: de app vraagt de provider dan een nieuwe stream die op het gekozen punt van het programma begint.

## Beveiliging
- De app draait zonder beheerdersrechten, schrijft alleen naar `%APPDATA%\IptvPlayer` en gebruikt het register niet.
- Alleen links met http, https, rtsp, rtmp, rtp of udp worden afgespeeld; `smb://`, `file://` e.d. uit een afspeellijst worden genegeerd.
- Typ je bij een Xtream-server geen `http://` of `https://`, dan probeert de app eerst https. Bronnen die via http lopen krijgen een waarschuwing (je inlog gaat dan onversleuteld over het netwerk).
- Downloads (EPG, JSON-antwoorden) hebben een maximale grootte. Opnames worden alleen van http(s)-streams gemaakt en het afspelen van bestanden is beperkt tot je eigen opnames.
- NuGet-pakketten staan op vaste versies (`packages.lock.json`); `dotnet list package --vulnerable --include-transitive` meldt kwetsbaarheden.

## Bouwen en starten
Vereist: Windows 11 en de [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet run --project tests/IptvPlayer.Tests        # tests (geen extra packages nodig)
dotnet run --project src/IptvPlayer.Desktop        # app starten
dotnet publish src/IptvPlayer.Desktop -c Release -r win-x64 --self-contained false -o publish   # losse uitvoerbare versie
```
Het afspelen gebeurt met LibVLC (de motor van VLC); die komt mee via het NuGet-pakket `VideoLAN.LibVLC.Windows`.

## Opbouw
| Map | Inhoud |
|---|---|
| `src/IptvPlayer.Engine` | Logica zonder UI: M3U-/Xtream-/XMLTV-verwerking, opslag, sync en de viewmodels |
| `src/IptvPlayer.Desktop` | WPF-schermen en LibVLC-koppeling |
| `tests/IptvPlayer.Tests` | Tests, inclusief de volledige reis eerste import → update → review |

## Bekend / nog te doen
- Wachtwoorden en inlog-links (`sources.json`, `settings.json`, `personal.json` en de catalogus) worden versleuteld opgeslagen met AES-256-GCM; de sleutel (`key.dpapi`) is beschermd met Windows DPAPI en alleen te openen door jouw Windows-account op deze pc. Gebruik je de app onder een ander account of op een andere pc, dan zijn die bestanden daar niet te lezen (ze worden apart gezet als `.unreadable-…` en je voegt je bron opnieuw toe). Een versleuteld bestand werkt niet in een back-up naar een andere pc.
- Beschrijvingen van films staan niet in de lijst die de provider geeft; alleen series (en afleveringen) hebben een korte tekst.
- Een M3U-bron met series toont die als losse films (de afleveringen staan los in de lijst).
- Terugkijken is "best effort": het hangt af van wat de provider ondersteunt.
- Geen opnemen.


## EyePTV voor iPhone (EyePTV.Mobile)
Een aparte iPhone-app (.NET MAUI) die zelfstandig werkt, zonder pc. Ze gebruikt dezelfde Engine en viewmodels als de Windows-app.
Eerste versie: bronnen (M3U of Xtream) toevoegen, bijwerken en verwijderen; kiezen wat in je lijst komt; Zenders / Films / Series met zoeken, groepen en favorieten; een speler met pauze, 10 s terug, 30 s vooruit, een spoelbalk en verder kijken. Er is nog **geen** gids, terugkijken, herinneringen, ondertitelkeuze, reservezenders of casten.

### De IPA ophalen
Je hebt geen Mac nodig: GitHub bouwt de app in de cloud.
1. Open de repository op GitHub, tabblad **Actions**, werkstroom **iOS**.
2. Klik op de laatste groene run (of start er een met **Run workflow**) en download onderaan het artifact **EyePTV-ios-ipa**. Je krijgt een zip met `EyePTV.ipa`.
3. Een bouw duurt ongeveer 15 tot 30 minuten.

### Installeren met AltStore of Sideloadly (gratis Apple-ID)
De IPA is bewust **niet ondertekend**; AltStore en Sideloadly tekenen hem opnieuw met jouw Apple-ID.
- **Sideloadly** (Windows): iPhone via usb aansluiten, `EyePTV.ipa` in het venster slepen, je Apple-ID invullen en op *Start* drukken. Daarna op de iPhone: *Instellingen → Algemeen → VPN en apparaatbeheer* → je Apple-ID → *Vertrouw*. Zet ook *Ontwikkelaarsmodus* aan (*Instellingen → Privacy en beveiliging*).
- **AltStore**: installeer AltServer op de pc en AltStore op de iPhone (zie altstore.io), open dan in AltStore het tabblad *My Apps* en kies *+* om de IPA te kiezen.

### Beperkingen van een gratis Apple-ID
- Een zo geïnstalleerde app is **7 dagen geldig**; daarna start hij niet meer.
- Je mag maximaal **3 apps** tegelijk op deze manier hebben staan (en een beperkt aantal nieuwe app-id's per week).
- **Verlopen?** Installeer dezelfde (of een nieuwe) IPA gewoon opnieuw met Sideloadly of AltStore; je bronnen en lijst blijven behouden. AltStore kan dat automatisch verversen zolang AltServer op de pc draait en jullie op hetzelfde wifi-netwerk zitten.
- Wil je later 1 jaar geldigheid zonder pc? Dan is een betaald Apple Developer-account nodig; de bouw is daarop voorbereid (zie `CLAUDE.md`).
