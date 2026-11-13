# RFIDlibra

Eget Windows-program, der skal få bibliotekets FEIG RFID-læsere (fx den lille **ID ISC.PR101**) til at virke med Cicero Mobile, ligesom [NFClibra](https://github.com/kaspersvangren/NFClibra) gør det på telefonen.

**Status: testudgave 0.1.0.** Programmet taler endnu ikke med Cicero. Det finder læseren, viser de tags, der ligger på den, og logger alt, så vi kan se, hvad den gamle PR101 kan klare.

Seneste version: [RFIDlibra.exe](https://github.com/kaspersvangren/rfidlibra/releases/latest/download/RFIDlibra.exe)

## Sådan testes den

1. **Stop staffConnect link**, så den ikke holder fast i læseren: åbn *Tjenester* (`services.msc`), find tjenesten med "staffConnect" eller "Arbiter" i navnet, og vælg *Stop*. Start den igen bagefter.
2. Sæt PR101'eren i USB.
3. Hent `RFIDlibra.exe` og kør den. Siger Windows "Windows beskyttede din pc", så vælg *Flere oplysninger* → *Kør alligevel*.
4. Programmet forbinder selv. Læg en bog på læseren.
5. Tryk **Selvtest af læsning**, mens bogen ligger der.
6. Tryk **Gem log på skrivebordet**, og send filen.

Programmet **skriver ikke** til tags. Det læser kun.

## Hvordan det virker

- USB-forbindelsen går gennem FEIG's `feusb.dll`, som Bibliothecas Tag Manager allerede har installeret (`C:\Program Files (x86)\bibliotheca\Common\TagManager(PM)\x64\feusb.dll`). En `feusb.dll` ved siden af `RFIDlibra.exe` bruges først, hvis den findes.
- Kommandoerne til læseren (FEIG's "ISO Host"-protokol med CRC) bygges af programmet selv.
- Tagindholdet fortolkes efter den danske datamodel, på samme måde som NFClibra og Deichmans [go-feig](https://github.com/digibib/go-feig).
- Bibliothecas staffConnect link får timeout på PR101, når den beder om mange blokke på én gang. RFIDlibra prøver først 8 blokke og går ned til 4, 2 og 1, hvis læseren ikke kan klare det.

Loggen gemmes også i `%LOCALAPPDATA%\RFIDlibra`.
