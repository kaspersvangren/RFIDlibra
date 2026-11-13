# RFIDlibra

Eget Windows-program, der får bibliotekets FEIG RFID-læsere (fx den lille **ID ISC.PR101** og MR101/MR102) til at virke med Cicero Mobile - ligesom [NFClibra](https://github.com/kaspersvangren/NFClibra) gør det på telefonen.

Seneste version: [RFIDlibra.exe](https://github.com/kaspersvangren/rfidlibra/releases/latest/download/RFIDlibra.exe)

## Sådan bruges den

1. Hent `RFIDlibra.exe` og start den. Siger Windows "Windows beskyttede din pc", så vælg *Flere oplysninger* → *Kør alligevel* (kun første gang).
2. Under **Indstillinger** slås *Start RFIDlibra, når jeg logger på Windows* til. Programmet lægger sig så i `%LOCALAPPDATA%\Programs\RFIDlibra` og starter derfra med skrankevinduet og et ikon ved uret.
3. I Cicero Mobile → Enhedsindstillinger → RFID: vælg leverandøren **Deichman**, adresse **localhost**, port **1667** (tag-dataformat "Danish Item").

Ikonet ved uret er **grønt**, når læseren er klar og Cicero lytter, **gult**, når læseren er klar men Cicero ikke lytter, og **rødt**, når der ikke er nogen læser. Klik på ikonet for at vise skrankevinduet, hvis det er skjult.

staffConnect link må ikke køre samtidig på samme pc - den tager læseren. RFIDlibra viser en advarsel, hvis den gør.

## Skrankevinduet

Et lille vindue uden kanter, der altid ligger øverst. Det kan trækkes rundt og husker sin plads, og det tager ikke fokus fra Cicero, når man klikker på det.

- Øverst: status (grøn prik = Cicero lytter, gul = Cicero lytter ikke, rød = ingen læser) og hvor mange bøger der ligger på læseren.
- Bøgerne som små etiketter med materialenummer og alarm (orange strimmel = alarm til, grøn = alarm fra). Et sæt vises som én etiket, fx "3 af 3 dele" - mangler der dele, står det med rødt.
- **Alarm fra** og **Alarm til** slår alarmen fra eller til på alle bøger på læseren. Vinduet blinker grønt eller orange, når alarmen skifter - også når Cicero gør det.
- Højreklik (eller klik på de tre prikker) giver menuen: **Værktøj til tags**, **Indstillinger**, **Fejlfinding**, **Skjul vinduet** og **Afslut**.

## De andre vinduer

- **Værktøj til tags** - se en chip, slå alarmen til/fra, nulstil den, eller programmér med et materialenummer. Antal dele foreslås ud fra, hvor mange chips der ligger på læseren; det kan sættes op, men ikke under det antal. Alle chips på læseren programmeres på én gang, og mangler der dele, bliver man bedt om resten undervejs. Rækkefølgen er tilfældig, når flere skrives på én gang - skal cd 1 være del 1, lægges chippene på én ad gangen. Alarmen slås til efter programmering, og chips med indhold overskrives kun efter én bekræftelse for hele sættet.
- **Indstillinger** - biblioteksnummer til nye chips og start med Windows.
- **Fejlfinding** - log, selvtest og "forbind igen".

De tre vinduer er sider, som programmet selv viser i rene Edge-vinduer (`http://localhost:1667/app`). Kun programmet kender nøglen til sidernes funktioner.

## Hvordan det virker

- USB-forbindelsen går gennem FEIG's `feusb.dll`, som Bibliothecas Tag Manager har installeret (`bibliotheca\Common\TagManager(PM)\x64`). Kommandoerne (FEIG's "ISO Host"-protokol med CRC) bygges af programmet selv.
- Mod Cicero svarer programmet som Deichmans [go-feig](https://github.com/digibib/go-feig) på localhost:1667 (HTTP + server-sent events).
- Tagindholdet læses og skrives efter den danske datamodel, som i NFClibra og go-feig. Alt, der skrives, kontrolleres ved at læse tagget igen. Lås-kommandoer kan ikke sendes.
- PR101 med firmware 1.7 taber af og til et svar på USB (det gør den også med Bibliothecas software). RFIDlibra venter kort på svar og prøver straks igen - op til 3 gange på samme forbindelse, derefter efter en genåbning.

Logfiler: `%LOCALAPPDATA%\RFIDlibra\log` (gemmes i 14 dage).
