# RFIDlibra

Eget Windows-program, der skal få bibliotekets FEIG RFID-læsere (fx den lille **ID ISC.PR101**) til at virke med Cicero Mobile, ligesom [NFClibra](https://github.com/kaspersvangren/NFClibra) gør det på telefonen.

**Status: testudgave 0.5.0.** Første udgave, der taler med Cicero Mobile. Programmet er et almindeligt vindue, der skal være åbent; en udgave i systembakken kommer senere.

## Opsætning i Cicero Mobile

I Enhedsindstillinger → RFID: vælg leverandøren **Deichman**, adresse **localhost**, port **1667**. Tag-dataformatet skal være "Danish Item". Vinduet viser "Cicero: lytter", når Cicero har forbindelse.

staffConnect link må ikke køre samtidig på samme pc, for den tager læseren.

Seneste version: [RFIDlibra.exe](https://github.com/kaspersvangren/rfidlibra/releases/latest/download/RFIDlibra.exe) (64-bit) og [RFIDlibra-32bit.exe](https://github.com/kaspersvangren/rfidlibra/releases/latest/download/RFIDlibra-32bit.exe) (til 32-bit feusb-filer, fx fra den gamle Bibliotheca-software)

## Sådan testes den

1. **Få staffConnect link til at slippe læseren**: stop tjenesten i *Tjenester* (`services.msc`, kræver administrator), eller sæt forbindelsen til COM i staffConnects egne indstillinger. Sæt det tilbage bagefter.
2. Sæt PR101'eren i USB.
3. Hent `RFIDlibra.exe` og kør den. Siger Windows "Windows beskyttede din pc", så vælg *Flere oplysninger* → *Kør alligevel*.
4. Programmet forbinder selv. Læg en bog på læseren.
5. Tryk **Selvtest af læsning**, mens bogen ligger der.
6. Tryk **Gem log på skrivebordet**, og send filen.

Programmet **skriver ikke** til tags. Det læser kun.

## Hvordan det virker

- USB-forbindelsen går gennem FEIG's `feusb.dll`, som Bibliotheca allerede har installeret. Programmet leder efter `feusb*.dll` ved siden af sig selv, i mapperne `RFIDIF`/`RFIDIFServer` på skrivebordet, i Tag Manager (`bibliotheca\Common\TagManager(PM)\x64` og `x86`) og i den gamle `Bibliotheca RFID\RFIDIF`. Filen vælges i listen øverst; valget huskes. 64-bit-filer kræver `RFIDlibra.exe`, 32-bit-filer `RFIDlibra-32bit.exe`.
- Kommandoerne til læseren (FEIG's "ISO Host"-protokol med CRC) bygges af programmet selv.
- Tagindholdet fortolkes efter den danske datamodel, på samme måde som NFClibra og Deichmans [go-feig](https://github.com/digibib/go-feig).
- PR101 med firmware 1.7 taber af og til et svar på USB (timeout), uanset hvor meget der læses, og uanset feusb-version (5.0 og 6.0 er afprøvet). Bibliothecas nye staffConnect link går i stå på det. RFIDlibra venter kun 150 ms på svar (500 ms ved søgning efter tags) og prøver automatisk igen: op til 3 gange på samme forbindelse, derefter efter en genåbning. Selvtesten undersøger, om en pause mellem kommandoerne får læseren til at tabe færre svar.

Loggen gemmes også i `%LOCALAPPDATA%\RFIDlibra`.
