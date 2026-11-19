# RFIDlibra

Eget Windows-program, der får bibliotekets FEIG RFID-læsere (fx den lille **ID ISC.PR101** og MR101/MR102) til at virke med Cicero Mobile og Cicero LMS - ligesom [NFClibra](https://github.com/kaspersvangren/NFClibra) gør det på telefonen.

Seneste version: [RFIDlibra.exe](https://github.com/kaspersvangren/rfidlibra/releases/latest/download/RFIDlibra.exe)

## Sådan bruges den

Pc'en skal have FEIG's USB-driver ("FEIG ID USB Driver" til Windows 10/11, hos os fra Software Center). Bibliothecas software er ikke nødvendig.

1. Hent `RFIDlibra.exe` og start den. Siger Windows "Windows beskyttede din pc", så vælg *Flere oplysninger* → *Kør alligevel* (kun første gang).
2. Under **Indstillinger** slås *Start RFIDlibra, når jeg logger på Windows* til. Programmet lægger sig så i `%LOCALAPPDATA%\Programs\RFIDlibra` og starter derfra med skrankevinduet og et ikon ved uret.
   **For alle brugere på en pc:** under Indstillinger → *Installer for alle brugere*. Kræver administrative rettigheder – Windows beder om dem, når du trykker. Programmet og FEIG's `feusb.dll` lægges i `C:\Program Files\RFIDlibra`, en genvej i den fælles Start-mappe starter det for alle, der logger på, og biblioteksnummeret gemmes til hele pc'en i `C:\ProgramData\RFIDlibra`. En ny version installeres på samme måde (kræver igen administrator). *Fjern for alle brugere* gør det modsatte.
3. I Cicero Mobile → Enhedsindstillinger → RFID: vælg leverandøren **Deichman**, adresse **localhost**, port **1667** (tag-dataformat "Danish Item"). Den gamle Cicero og sandkassen kan sættes op på samme måde.

Ikonet ved uret er **grønt**, når læseren er klar og Cicero lytter, **gult**, når læseren er klar men Cicero ikke lytter, og **rødt**, når der ikke er nogen læser. Klik på ikonet for at vise skrankevinduet, hvis det er skjult.

staffConnect link må ikke køre samtidig på samme pc - den tager læseren. RFIDlibra viser en advarsel, hvis den gør.

## Skrankevinduet

Et lille vindue uden kanter, der altid ligger øverst. Det kan trækkes rundt og husker sin plads, og det tager ikke fokus fra Cicero, når man klikker på det.

- Øverst: status (grøn prik = Cicero lytter, gul = Cicero lytter ikke, fx når læsningen er slået fra i Cicero, rød = ingen læser) og hvor mange bøger der ligger på læseren.
- Bøgerne som små etiketter med materialenummer og alarm (orange strimmel = alarm til, grøn = alarm fra). Et sæt vises som én etiket, fx "3 af 3 dele" - mangler der dele, står det med rødt.
- **Alarm fra** og **Alarm til** slår alarmen fra eller til på alle bøger på læseren. Vinduet blinker grønt eller orange, når alarmen skifter - også når Cicero gør det.
- Højreklik (eller klik på de tre prikker) giver menuen: **Kompakt visning**, **Værktøj til tags**, **Indstillinger**, **Fejlfinding**, **Skjul vinduet** og **Afslut**.
- **Kompakt visning** er én smal stribe: status, antal bøger og knapperne **Fra** og **Til**. Mangler der en del af et sæt, står det med rødt ("Mangler del 2 af 3"). Hold musen over striben for at se materialenumrene. Valget huskes.

## De andre vinduer

- **Værktøj til tags** - et fast vindue (ca. 700 × 440, kan ikke ændre størrelse, ingen rullebjælke). Til venstre chippene på læseren: klik på en for at se den, slå alarmen til/fra eller nulstille den. Til højre programmering med materialenummer. Antal dele foreslås ud fra, hvor mange chips der ligger på læseren; det kan sættes op, men ikke under det antal. Alle chips på læseren programmeres på én gang, og mangler der dele, bliver man bedt om resten undervejs. Rækkefølgen er tilfældig, når flere skrives på én gang - skal cd 1 være del 1, lægges chippene på én ad gangen. Alarmen slås til efter programmering, og chips med indhold overskrives kun efter én bekræftelse for hele sættet.
- **Indstillinger** - biblioteksnummer til nye chips, start med Windows, installation for alle brugere, tjek efter nye versioner og "Om".
- **Fejlfinding** - log, selvtest og "forbind igen".

Indstillinger og Fejlfinding er sider, som programmet selv viser i rene Edge-vinduer (`http://localhost:1667/app`). Kun programmet kender nøglen til sidernes funktioner.

## Hvordan det virker

- USB-forbindelsen går gennem FEIG's `feusb.dll`. RFIDlibra har FEIG's egen udgave med indbygget (64-bit, version 6.0.0.0, fra FEIG's ID USB-driverpakke v3.26) og lægger den i `%LOCALAPPDATA%\RFIDlibra\feig` første gang. Ligger der en `feusb.dll` ved siden af programmet (fx i Program Files), bruges den i stedet. En `feusb.dll`, der allerede ligger, røres ikke. Se [feig/LÆSMIG.md](feig/LÆSMIG.md) om FEIG's licens. Kommandoerne (FEIG's "ISO Host"-protokol med CRC) bygges af programmet selv.
- Mod Cicero svarer programmet som Deichmans [go-feig](https://github.com/digibib/go-feig) på localhost:1667 (HTTP + server-sent events).
- Tagindholdet læses og skrives efter den danske datamodel, som i NFClibra og go-feig. Alt, der skrives, kontrolleres ved at læse tagget igen. Lås-kommandoer kan ikke sendes.
- PR101 med firmware 1.7 taber af og til et svar på USB (det gør den også med Bibliothecas software). RFIDlibra venter kort på svar og prøver straks igen - op til 3 gange på samme forbindelse, derefter efter en genåbning.
- PR101/MR101 (firmware 1.x) når ikke at skrive alle 8 blokke på en chip i ét hug - de stopper efter blok 4. Derfor skriver RFIDlibra blok for blok på dem, med kontrolsummen (blok 4 og 5) sidst, så en halvt skrevet chip altid ses som ugyldig. Alt skrevet kontrolleres ved at læse chippen igen.

Logfiler: `%LOCALAPPDATA%\RFIDlibra\log` (gemmes i 14 dage).

## Nye versioner

Under Indstillinger kan *Tjek efter nye versioner* slås til (det er slået fra fra start). RFIDlibra spørger så GitHub ved start og én gang i døgnet og viser en lille besked ved uret, når der er en ny version, med knapperne *Se hvad der er nyt*, *Senere* og *Vis ikke igen* (gælder kun den version). Programmet henter eller installerer aldrig noget selv.

## Hvem har lavet den

- Idé, design og test: Kasper Svangren, Gladsaxe Bibliotekerne
- Kode: Claude (AI fra Anthropic)

## Licens

RFIDlibra: MIT - se [LICENSE](LICENSE). Testet med FEIG ID ISC.PR101, MR101 og MR102.

`feig/feusb.dll` er FEIG ELECTRONIC GmbH's og følger med uændret under FEIG's egen licens - se [feig/LÆSMIG.md](feig/LÆSMIG.md). FEIG's USB-driver følger ikke med og installeres separat.
