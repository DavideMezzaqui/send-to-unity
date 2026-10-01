# Invia a Unity

Seleziona dei file in Esplora risorse, tasto destro, **Invia al progetto Unity...**
e scegli progetto e cartella di destinazione. Niente trascinamenti.

## Installazione
1. Copia questa cartella su Windows.
2. Tasto destro su `install.ps1`, "Esegui con PowerShell" (nessun permesso di amministratore).

Disinstallazione: `uninstall.ps1`.

## Uso
- Progetti aperti: rilevati dai processi `Unity.exe` e mostrati per primi con `[aperto]`.
- Progetti chiusi: letti dalla lista di Unity Hub. Se manca, usa "Aggiungi...".
  I file vengono copiati lo stesso e Unity li importa alla prossima apertura.
- La cartella di destinazione viene ricordata per ogni progetto.
- Con Unity aperto, la finestra viene portata in primo piano, cosi' parte l'import automatico.
- Windows 11: la voce e' sotto "Mostra altre opzioni".
- Oltre 15 file selezionati Windows nasconde la voce del tasto destro: usa
  **Invia a > Progetto Unity**, che riceve tutti i file in una volta.
