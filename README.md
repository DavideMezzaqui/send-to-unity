# Invia a Unity

Manda file e cartelle da Esplora risorse dentro un progetto Unity con il tasto destro:
niente trascinamenti, niente finestre affiancate.

![Il menu del tasto destro](docs/menu.png)

## Come si usa

Seleziona i file (o una cartella), tasto destro > **Invia a Unity** > clic sul progetto.

- **La prima volta** che usi un progetto si apre una finestra: scegli la cartella dentro
  `Assets` (puoi anche scriverne una nuova, viene creata).

  ![La finestra](docs/finestra.png)

- **Dalla seconda volta** il progetto compare nel menu con la sua cartella: un clic e i file
  sono dentro, senza finestre. Compare solo una notifica in basso a destra; cliccandola si apre
  la cartella con il file appena arrivato già selezionato. Se la copia è lunga (cartelle grandi,
  file pesanti) la notifica mostra l'avanzamento.

  ![La notifica](docs/notifica.png)

- I progetti usati più di recente stanno in alto. Arrivano da Unity Hub e dai progetti recenti
  di Unity; se ne manca uno: **Scegli progetto e cartella...** > **Sfoglia...**
  Due progetti con lo stesso nome si distinguono dalla cartella che li contiene.
- Se il progetto è **aperto in Unity**, a copia finita Unity viene portato in primo piano e
  importa subito i file. Se è chiuso, li importa alla prossima apertura.
- Se un file **esiste già** puoi sostituirlo o saltarlo. Sostituendo, il file `.meta` resta:
  riferimenti e impostazioni di import in Unity non si rompono. Premendo Invio si sostituisce.
  Se Unity sta ancora importando il vecchio file, il programma aspetta qualche secondo e riprova.

  ![File già presenti](docs/file-presenti.png)

- Funziona con molti file insieme (provato con 40) e con le cartelle, sottocartelle comprese.
  Copiando una cartella vengono saltati `.git`, `__MACOSX`, `Thumbs.db` e simili.
- Nella finestra c'è anche **Sposta**, che toglie i file dalla posizione originale.
- **File `.meta` di Unity**, se i file vengono da un altro progetto Unity:
  - copiando, il `.meta` arriva insieme al file, quindi le impostazioni di import restano;
  - spostando, il `.meta` segue sempre il file (e le cartelle), quindi nell'origine non restano
    `.meta` orfani e, dentro lo stesso progetto, i riferimenti non si rompono;
  - copiando dentro lo stesso progetto il `.meta` non viene duplicato (Unity ne crea uno nuovo);
  - un `.meta` che il progetto di destinazione ha già non viene mai sostituito.
- Da tastiera: tasto menu (o Maiusc+F10), **I**, freccia destra, Invio.

## Installazione

1. Scarica la cartella `send-to-unity` ed estraila.
2. Doppio clic su `install.cmd`.

Non servono permessi di amministratore né download: il programma viene compilato sul tuo PC
con il compilatore C# che fa già parte di Windows. Se compare "Windows ha protetto il PC",
clicca "Ulteriori informazioni" > "Esegui comunque" (succede con i file .cmd scaricati da internet).

Per aggiornare: rifai `install.cmd`. Per togliere tutto: `uninstall.cmd`.

## Da sapere

- **Windows 11**: la voce è nel menu classico, cioè sotto "Mostra altre opzioni". Più veloce:
  **Maiusc + tasto destro**, che apre subito il menu classico.
- Unity importa da solo solo se in *Preferences > Asset Pipeline* è attivo **Auto Refresh**
  e non sei in Play mode. Altrimenti premi Ctrl+R in Unity.
- C'è anche tasto destro > **Invia a** > **Progetto Unity**, che apre direttamente la finestra.
- Tutto quello che succede viene scritto in `%LOCALAPPDATA%\SendToUnity\log.txt`:
  se qualcosa non va, è il file da guardare (o da mandare).

## Per chi lo modifica

- `SendToUnity.cs` è tutto il programma. Deve restare compatibile con C# 5, perché lo compila
  il compilatore incluso in Windows.
- `tests/test-windows.ps1` lo installa e lo prova su una vera macchina Windows (GitHub Actions,
  a ogni modifica di questa cartella): menu del tasto destro vero, finestre, copie, `.meta`,
  file bloccati, progetto aperto in un finto Unity, disinstallazione.
