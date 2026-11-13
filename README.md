# RFIDlibra

Eget Windows-program, der skal få bibliotekets FEIG RFID-læsere (fx den lille **ID ISC.PR101**) til at virke med Cicero Mobile, ligesom [NFClibra](https://github.com/kaspersvangren/NFClibra) gør det på telefonen.

**Status: testudgave 0.2.0.** Programmet taler endnu ikke med Cicero. Det finder læseren, viser de tags, der ligger på den, og logger alt, så vi kan se, hvad den gamle PR101 kan klare.

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
- Bibliothecas staffConnect link får timeout på PR101, når den beder om mange blokke på én gang. RFIDlibra prøver først 8 blokke og går ned til 4, 2 og 1, hvis læseren ikke kan klare det.

Loggen gemmes også i `%LOCALAPPDATA%\RFIDlibra`.
