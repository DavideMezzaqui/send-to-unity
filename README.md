# Invia a Unity

Seleziona dei file in Esplora risorse, tasto destro, **Invia al progetto Unity...**,
scegli progetto e cartella. Niente trascinamenti, niente finestre affiancate.

## Installazione
1. Scarica la cartella `send-to-unity` su Windows.
2. Doppio clic su `install.cmd`. Non servono permessi di amministratore.

Per disinstallare: doppio clic su `uninstall.cmd`.

## Uso
- **Progetti aperti**: compaiono in cima con `[aperto]`.
- **Progetti chiusi**: presi da Unity Hub e dai progetti recenti di Unity.
  Se uno manca, usa "Aggiungi...". I file vengono copiati e Unity li importa alla prossima apertura.
- **Cartella di destinazione**: scegline una esistente o scrivine una nuova (es. `Assets/Textures/Rocce`).
  Viene ricordata per ogni progetto, quindi dalla seconda volta basta premere Invio.
- **File gia' presenti**: puoi sovrascriverli (il `.meta` resta, quindi i riferimenti in Unity non si rompono) o saltarli.
- **Cartelle**: funzionano tramite "Invia a"; il contenuto viene unito a quello esistente.
- Con Unity aperto la sua finestra viene portata in primo piano, cosi' l'import parte subito.

## Limiti
- Windows 11: la voce e' sotto "Mostra altre opzioni" (o Maiusc + clic destro).
- Oltre 15 file Windows nasconde la voce del tasto destro: usa **Invia a > Progetto Unity**.
- Unity importa da solo solo se "Auto Refresh" e' attivo (Preferences > Asset Pipeline) e non sei in Play mode.
  Altrimenti premi Ctrl+R in Unity.
- Usa VBScript per avviarsi senza finestre nere. Oggi e' attivo su Windows 10 e 11; Microsoft ha annunciato
  che in futuro sara' disattivato di default. Se succede, la voce smette di funzionare: dimmelo e lo sostituiamo.
