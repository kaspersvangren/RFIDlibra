# Ændringer

- **0.2.0** – 32-bit udgave (RFIDlibra-32bit.exe), så de gamle 32-bit feusb-filer fra den gamle Bibliotheca-software kan afprøves. Liste over fundne feusb-filer med version og 32/64-bit; vælg én, og programmet genstarter med den. Selvtesten prøver hver størrelse 3 gange og skriver et samlet resultat. Tæller for kommandoer og timeouts i statuslinjen.
- **0.1.0** – Første testudgave: finder FEIG-læseren via feusb.dll, finder selv rammeformat og adresse, viser tags (materialenummer, del, bibliotek, alarm) og logger rå trafik. Selvtest prøver at læse 1, 2, 4, 8, 9, 16 og 28 blokke på én gang. Skriver ikke til tags.
