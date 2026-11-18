# Holm Feierabend

Holm mit einem Zusatz: die totale Gleitzeit aus dem BBC-Extranet.

Zugeklappt steht der Saldo in der Insel, während einer kurzen Pause das Wort **Pause**. Aufgeklappt stehen zwei Sätze:

- **Wenn du jetzt gehst** ist der Gleitzeit-Saldo.
- **Bis neutral** ist die Dauer ab jetzt, bis dieser Saldo null wäre. Liegt er schon darüber, steht dort `0:00`.

Solange im Extranet eine Buchung ohne Ende offen ist, läuft die Zahl jede Sekunde weiter. Sobald ein Ende gesetzt ist, bleibt sie auf dem Saldo stehen, den das Extranet meldet. Die Notch trägt nichts ein.

Eine Lücke unter 30 Minuten ohne offene Buchung ist die Pause. Die Gleitzeit bewegt sich dann nicht. Sind die 30 Minuten voll und keine Buchung offen, steht dort **Ausgestempelt**. Der Saldo kommt vom Extranet, nicht aus dem offenen Monatsabschluss.

## Anmelden

Einstellungen, Schalter **Gleitzeit**, Knopf **Anmelden**. Jede Person nutzt das eigene BBC-Konto. Das Passwort geht nur ans Extranet und wird nicht gespeichert. Danach bleibt die Sitzung verschlüsselt für das Windows- oder Linux-Konto auf diesem PC.

Im Repository liegen keine Passwörter und keine geheimen Schlüssel. Die Client-ID ist die öffentliche der Extranet-Webseite.

## Start

Windows 10 (2004 oder neuer) oder Windows 11, x64. `Holm.exe` im Repository ist die fertige Fassung 1.15.3, eine einzelne Datei ohne .NET-Installation. Sie darf in einen beschreibbaren Ordner kopiert werden. Einstellungen, dann **Updates**: eine neuere Veröffentlichung dieses Repositories ersetzt genau diese Datei und startet neu. Eine Entwicklungsfassung aus `dotnet run` kann das nicht.

Zum Entwickeln das .NET 10 SDK:

```powershell
dotnet run --project Holm.App
```

Für Mitarbeitende ohne SDK legt das Skript eine eigene Kopie ab und einen Startmenü-Eintrag. Eine schon installierte Holm-Fassung bleibt, wo sie ist.

```powershell
powershell -ExecutionPolicy Bypass -File tools\Install-HolmFeierabend.ps1
```

Updates suchen Releases von diesem Repository. Ein Update ersetzt diese Fassung nicht durch Holm ohne Gleitzeit.

## Herkunft

Die Notch ist [Holm](https://github.com/ademerci/Holm) von ademerci, MIT-Lizenz, Copyright (c) 2026 ademerci. Diese Fassung nennt Holm und ändert die Update-Adresse auf `dnd67/holm-feierabend`.
