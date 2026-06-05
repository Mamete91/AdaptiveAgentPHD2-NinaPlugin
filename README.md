# Adaptive Agent for PHD2 — Dashboard (NINA Plugin)

Plugin minimale per NINA che aggiunge un pannello dockable con la dashboard web
dell'**Adaptive Agent for PHD2**, esposta su `http://localhost:8080`.

---

## Cosa fa

Apre, all'interno di NINA, la dashboard dell'Agente tramite un controllo WebView2.
Da **v1.1** aggiunge sopra il pannello un **badge di stato** dell'Agente e un pulsante
**Avvia Adaptive Agent**. Nient'altro: nessuna logica adattiva, nessun contatto con
PHD2, nessuna interferenza col Sequencer. Tutta la logica vive nel processo Python separato.

---

## v1.1 — Launch Agent + badge stato

Due rifiniture UX leggere che condividono un piccolo poller (`GET /about` ogni 15s, configurabile):

- **Badge di stato** sopra il WebView:
  - 🟢 **Agente online v2.2** (verde) quando l'Agente risponde su `/about`.
  - ⚪ **Agente offline** (grigio) quando non risponde.
- **Pulsante "Avvia Adaptive Agent"** sopra il WebView:
  - Abilitato solo quando l'Agente è **offline** *e* il percorso al `Avvia.bat` è configurato.
  - Disabilitato quando l'Agente è già **online** (tooltip "Agente già in esecuzione").
  - Se il percorso non è impostato, mostra "Configura percorso Avvia.bat nelle settings".

Questa versione **non** mette in pausa la sequenza NINA e **non** interagisce col Sequencer:
è puramente una comodità per evitare di aprire Esplora Risorse per lanciare l'Agente.

### Configurazione (prima volta)

In NINA: **Options → Plugins → Adaptive Agent for PHD2 — Dashboard**. Imposta:

| Impostazione | Default | Note |
|--------------|---------|------|
| **Percorso Avvia.bat** | *(vuoto)* | Es. `...\PHD2_Assist_PATCHED\Pacchetto_Distribuzione\Avvia.bat`. Usa "Sfoglia...". |
| **Intervallo controllo stato (s)** | `15` | Range 5–120. Più basso = badge più reattivo. |
| **URL dashboard** | `http://localhost:8080` | Cambialo solo se hai modificato la porta dell'Agente. |

Le impostazioni si salvano automaticamente in
`%LOCALAPPDATA%\NINA\Plugins\AdaptiveAgentForPHD2.NinaPlugin\settings.json`.

---

## Prerequisiti

| Requisito | Note |
|-----------|------|
| **NINA 3.3+** | Versione installata sul tuo PC |
| **Adaptive Agent for PHD2** | Il pacchetto Python deve essere in esecuzione (`Avvia.bat`) |
| **Microsoft Edge / WebView2 Runtime** | Pre-installato su Windows 11 e Windows 10 aggiornato. Se il pannello appare bianco, installa manualmente il runtime dal sito Microsoft: https://developer.microsoft.com/en-us/microsoft-edge/webview2/ |

---

## Build

Richiede **.NET 8 SDK** (x64).

```powershell
cd src\AdaptiveAgentForPHD2.NinaPlugin
dotnet build -c Release
```

Output: `bin\Release\AdaptiveAgentForPHD2.NinaPlugin.dll`

---

## Installazione

Dalla root del repository:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-plugin.ps1
```

Il plugin viene copiato in:
```
%LOCALAPPDATA%\NINA\Plugins\3.0.0\AdaptiveAgentForPHD2.NinaPlugin.dll
```

Riavvia NINA per caricarlo.

---

## Utilizzo

1. Apri NINA e, dal menu **Window**, cerca **Adaptive Agent for PHD2**. Aggancia il pannello.
2. (v1.1) La prima volta, imposta il percorso al `Avvia.bat` nelle settings del plugin
   (vedi *Configurazione* sopra).
3. Avvia l'Agente: clicca **Avvia Adaptive Agent** (oppure lancia `Avvia.bat` manualmente).
4. Entro qualche secondo il badge passa a 🟢 **Agente online vX.Y** e la dashboard si carica.
5. Usa il pulsante **Reload** per ricaricare la pagina manualmente.

Se l'Agente non e' in esecuzione, il badge resta ⚪ **Agente offline** e compare un pannello
di fallback con il pulsante **Riprova**.

---

## Limiti noti v1.1

- **WebView2 Runtime**: su Windows 10 datati il pannello puo' apparire bianco se
  il runtime non e' installato (vedi Prerequisiti).
- **Solo localhost**: il poller e il WebView puntano all'host della dashboard
  configurata (default `http://localhost:8080`); è pensato per un Agente sulla
  stessa macchina di NINA.
- **Nessuna auto-pause**: il plugin non mette in pausa la sequenza NINA né reagisce a
  `/status`. È una pura rifinitura UX (eventuale auto-pause valutabile in futuro).
- **Testato su NINA 3.3**: versioni precedenti non supportate esplicitamente.

> Risolti in v1.1 rispetto a v1.0: URL ora configurabile nelle settings; health-check
> proattivo ogni 5–120 s con badge di stato.

---

## Supporto

Gruppo Telegram della community: https://t.me/+eewRNpvElSs5OWY8

---

## Licenza

Copyright (c) 2026 Alessandro Curci — All rights reserved.
