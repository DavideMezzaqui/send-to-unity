# Invia a Unity

Manda file e cartelle da Esplora risorse dentro un progetto Unity con il tasto destro:
niente trascinamenti, niente finestre affiancate.

## Come si usa

Seleziona i file (o una cartella), tasto destro > **Invia a Unity** > scegli il progetto.

- **La prima volta** che usi un progetto si apre una finestra: scegli la cartella dentro
  `Assets` (puoi anche scriverne una nuova, viene creata).
- **Dalla seconda volta** il progetto compare nel menu con la sua cartella, per esempio
  `MioGioco → Assets/Textures`: un clic e i file sono dentro. Nessuna finestra, solo una
  notifica in basso a destra (cliccandola si apre la cartella).
- Per cambiare progetto o cartella: **Scegli progetto e cartella...** in fondo al sottomenu.
- Se il progetto è **aperto in Unity**, Unity viene portato in primo piano e importa subito
  i file. Se è chiuso, li importa alla prossima apertura.
- Se un file **esiste già** ti chiede se sostituirlo. Sostituendo, il file `.meta` resta:
  riferimenti e impostazioni di import in Unity non si rompono.
- Con **Sposta** (nella finestra) i file vengono tolti dalla posizione originale.

I progetti arrivano da Unity Hub e dai progetti recenti di Unity, i più usati in alto.
Se ne manca uno: **Scegli progetto e cartella...** > **Sfoglia...**

## Installazione

1. Scarica la cartella `send-to-unity` ed estraila.
2. Doppio clic su `install.cmd`.

Non servono permessi di amministratore né download: il programma viene compilato sul tuo PC
con il compilatore C# che fa già parte di Windows. Se compare "Windows ha protetto il PC",
clicca "Ulteriori informazioni" > "Esegui comunque" (succede con i file .cmd scaricati da internet).

Per aggiornare: rifai `install.cmd`. Per togliere tutto: `uninstall.cmd`.

## Limiti

- **Windows 11**: la voce è sotto "Mostra altre opzioni". Più veloce: **Maiusc + tasto destro**,
  che apre subito il menu completo.
- **Più di 15 file**: Windows nasconde le voci di questo tipo. Usa tasto destro >
  **Invia a** > **Progetto Unity**, che funziona con qualsiasi numero di file.
- Unity importa da solo solo se in *Preferences > Asset Pipeline* è attivo **Auto Refresh**
  e non sei in Play mode. Altrimenti premi Ctrl+R in Unity.

## Se qualcosa non va

Tutto quello che succede viene scritto in `%LOCALAPPDATA%\SendToUnity\log.txt`.

## Per chi lo modifica

- `SendToUnity.cs` è tutto il programma. Deve restare compatibile con C# 5, perché lo compila
  il compilatore incluso in Windows.
- `tests/test-windows.ps1` lo installa e lo prova su una vera macchina Windows: gira su
  GitHub Actions a ogni modifica di questa cartella.
