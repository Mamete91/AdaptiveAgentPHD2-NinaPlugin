# Adaptive Agent for PHD2 — Dashboard (NINA Plugin)

Plugin minimale per NINA che aggiunge un pannello dockable con la dashboard web
dell'**Adaptive Agent for PHD2**, esposta su `http://localhost:8080`.

---

## Cosa fa

Apre, all'interno di NINA, la dashboard dell'Agente tramite un controllo WebView2.
Da **v1.1** aggiunge sopra il pannello un **badge di stato** dell'Agente e un pulsante
**Avvia Adaptive Agent**; da **v1.3** inoltra all'Agente le **metriche per-posa** di NINA
(HFR, conteggio stelle, statistiche immagine) a ogni light salvata; da **v1.4** il **Safety
Monitor** dichiara unsafe anche sulle **nubi** (trasparenza CLOUD persistente), non solo su
STAR_LOST. Nessuna logica adattiva nel plugin: la trasparenza è riconosciuta dall'Agente
(processo Python separato); il plugin è ponte di telemetria/UX + segnale di sicurezza.

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
| **Percorso Avvia.bat** | *(vuoto)* | Es. `...\AdaptiveAgentPHD2\Pacchetto_Distribuzione\Avvia.bat`. Usa "Sfoglia...". |
| **Intervallo controllo stato (s)** | `15` | Range 5–120. Più basso = badge più reattivo. |
| **URL dashboard** | `http://localhost:8080` | Cambialo solo se hai modificato la porta dell'Agente. |

Le impostazioni si salvano automaticamente in
`%LOCALAPPDATA%\NINA\Plugins\AdaptiveAgentForPHD2.NinaPlugin\settings.json`.

---

## v1.3 — Inoltro telemetria per-posa all'Agente (§42)

A ogni light frame salvata, il plugin si iscrive a `IImageSaveMediator.ImageSaved` e
inoltra all'Agente le metriche per-posa di NINA via `POST <URL dashboard>/nina/telemetry`
(contratto `schema_version=1`): **HFR**, **HFR std**, **conteggio stelle**, **statistiche
immagine** (mean/median/stdev ADU), **durata posa** e **filtro**.

- **Opzionale e graceful**: se l'Agente è offline (o il toggle è off) non succede nulla —
  nessuna eccezione, nessun popup, NINA prosegue la sequenza. L'inoltro è *fire-and-forget*
  con timeout breve (3 s) e nessun retry (la posa successiva riprova da sé).
- **Toggle**: *«Inoltra le metriche per-posa di NINA all'Agente»* nelle settings del plugin
  (default **attivo**). Disattivandolo, il plugin resta in ascolto ma non invia alcun dato.
- **Requisito lato Agente**: l'endpoint `POST /nina/telemetry` esiste dall'Agente **§41**
  (v2.6). L'Agente espone i dati ricevuti in `GET /status` sotto il blocco `nina`
  (`connected`, `metrics`…); dopo ~3 min senza nuove pose torna `connected:false`
  conservando l'ultimo valore. Nessun consumatore agisce ancora sui dati: è il "tubo" su
  cui poggeranno le feature successive (context-gating, indice di trasparenza, ecc.).
- **Nota SDK**: **FWHM** (arcsec) ed **eccentricità** non sono esposti da
  `IStarDetectionAnalysis` in NINA **3.2.0.9001** → vengono **omessi** finché la NINA
  installata non li espone (build successive); il contratto è già predisposto a riceverli.

---

## v1.4 — Safety su nubi (N6): il Safety Monitor ferma la ripresa sulle nubi (§49)

Il **Safety Monitor** virtuale ora dichiara **unsafe** — accanto alla condizione STAR_LOST
esistente — anche quando la **trasparenza del cielo** (riconosciuta dall'Agente dai light
NINA: conteggio stelle + fondo) resta **CLOUD** per alcuni poll consecutivi. Così NINA può
mettere in pausa la ripresa **prima** di perdere la stella di guida (niente light sprecati
sotto le nuvole).

- **Isteresi asimmetrica** (tarabile nelle settings): **lento** verso unsafe (`CLOUD → unsafe`
  dopo N poll, default 8), **più rapido** verso safe (`CLEAR/HAZE → safe` dopo M poll, default
  4). Una velatura breve (HAZE transitorio) NON manda unsafe.
- **Fail-safe:** se l'Agente è spento o la telemetria è stantia, la condizione nubi è **neutra**
  (nessun unsafe spurio) — resta attiva solo la logica STAR_LOST come backstop.
- **Toggle** "Safety su nubi attiva" (default ON) + soglie N/M nelle settings del plugin.
- **Confine invariato:** il plugin **segnala** unsafe/safe (con la causa: STAR_LOST o CLOUD);
  **NINA decide** cosa farne (pausa/park) secondo la policy safety configurata.
- Version-agnostic: legge `/status` come JSON puro → gira su NINA **3.2 e 3.3**.

> **⚠️ Nota utente (importante):** perché la protezione sia **continua** durante la ripresa,
> in NINA la sequenza deve avere un blocco **"Wait Until Safe" DENTRO il loop** di
> acquisizione (non solo all'inizio). Altrimenti il Safety Monitor viene consultato una volta
> sola e le nubi che arrivano a metà sequenza non mettono in pausa.

---

## Prerequisiti

| Requisito | Note |
|-----------|------|
| **NINA 3.2+** | Compatibile con NINA **3.2 e successive** (inclusa la 3.3). Versione installata sul tuo PC |
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
- **NINA 3.2+**: il plugin è compatibile con NINA **3.2 e successive** (inclusa la 3.3);
  compila sul SDK NINA 3.2.0.9001 e legge `/status` come JSON puro. Versioni precedenti
  alla 3.2 non supportate.

> Risolti in v1.1 rispetto a v1.0: URL ora configurabile nelle settings; health-check
> proattivo ogni 5–120 s con badge di stato.

---

## Supporto

Gruppo Telegram della community: https://t.me/+eewRNpvElSs5OWY8

---

## Licenza

Distribuito con licenza **BSD-3-Clause** — vedi il file [`LICENSE`](LICENSE).

Copyright (c) 2026 Alessandro Curci.
