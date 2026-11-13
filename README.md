# RFIDlibra

Eget Windows-program, der får bibliotekets FEIG RFID-læsere (fx den lille **ID ISC.PR101** og MR101/MR102) til at virke med Cicero Mobile - ligesom [NFClibra](https://github.com/kaspersvangren/NFClibra) gør det på telefonen.

Seneste version: [RFIDlibra.exe](https://github.com/kaspersvangren/rfidlibra/releases/latest/download/RFIDlibra.exe)

## Sådan bruges den

1. Hent `RFIDlibra.exe` og start den. Siger Windows "Windows beskyttede din pc", så vælg *Flere oplysninger* → *Kør alligevel* (kun første gang).
2. Under **Indstillinger** slås *Start RFIDlibra, når jeg logger på Windows* til. Programmet lægger sig så i `%LOCALAPPDATA%\Programs\RFIDlibra` og starter derfra i baggrunden med et ikon ved uret.
3. I Cicero Mobile → Enhedsindstillinger → RFID: vælg leverandøren **Deichman**, adresse **localhost**, port **1667** (tag-dataformat "Danish Item").

Ikonet ved uret er **grønt**, når læseren er klar og Cicero lytter, **gult**, når læseren er klar men Cicero ikke lytter, og **rødt**, når der ikke er nogen læser. Klik på ikonet for at åbne vinduet.

staffConnect link må ikke køre samtidig på samme pc - den tager læseren. RFIDlibra viser en advarsel, hvis den gør.

## Vinduet

- **På læseren** - de bøger, læseren ser lige nu, med materialenummer, del og alarm.
- **Værktøj til tags** - se en chip, slå alarmen til/fra, nulstil den, eller programmér den med et materialenummer (også sæt i flere dele, én chip ad gangen). Alarmen slås til efter programmering, og en chip med indhold overskrives kun efter bekræftelse.
- **Indstillinger** - biblioteksnummer til nye chips og start med Windows.
- **Fejlfinding** - log, selvtest og "forbind igen".

Vinduet er en side, som programmet selv viser i et rent Edge-vindue (`http://localhost:1667/app`). Kun programmet kender nøglen til sidens funktioner.

## Hvordan det virker

- USB-forbindelsen går gennem FEIG's `feusb.dll`, som Bibliothecas Tag Manager har installeret (`bibliotheca\Common\TagManager(PM)\x64`). Kommandoerne (FEIG's "ISO Host"-protokol med CRC) bygges af programmet selv.
- Mod Cicero svarer programmet som Deichmans [go-feig](https://github.com/digibib/go-feig) på localhost:1667 (HTTP + server-sent events).
- Tagindholdet læses og skrives efter den danske datamodel, som i NFClibra og go-feig. Alt, der skrives, kontrolleres ved at læse tagget igen. Lås-kommandoer kan ikke sendes.
- PR101 med firmware 1.7 taber af og til et svar på USB (det gør den også med Bibliothecas software). RFIDlibra venter kort på svar og prøver straks igen - op til 3 gange på samme forbindelse, derefter efter en genåbning.

Logfiler: `%LOCALAPPDATA%\RFIDlibra\log` (gemmes i 14 dage).
