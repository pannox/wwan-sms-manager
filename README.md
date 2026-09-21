# WWAN SMS Manager

### App per **Windows 10 / Windows 11** che legge e scrive gli **SMS** dalle **schede 4G/LTE integrate** nel PC (modem Mobile Broadband).

Se hai un notebook con SIM / eSIM e modem cellulare (Fibocom, Intel XMM, Dell DW5820e, Quectel, …) e cerchi un modo per **vedere, inviare e cancellare gli SMS** senza l’app del produttore: questo è il programma.

> Windows riconosce la rete cellulare, ma **non offre un’app Messaggi** per il modem WWAN.  
> I software tipo “Mobile Manager” spesso non funzionano più su Windows 11.  
> **WWAN SMS Manager** usa le API ufficiali di Windows e funziona in modo portable.

---

## A cosa serve (in una frase)

**Leggere e scrivere SMS dalla scheda 4G del computer su Windows 11**, come faresti sul telefono.

## Cosa puoi fare

| Funzione | Descrizione |
|----------|-------------|
| **Leggere** | Inbox SMS salvati sulla SIM / memoria del modem |
| **Inviare** | Nuovi SMS a qualsiasi numero |
| **Cancellare** | Singoli messaggi o tutta la memoria del modem |
| **Esportare** | Salvataggio inbox in TXT o CSV |
| **Portabile** | Un solo `.exe`, nessuna installazione |

Gestisce anche gli SMS lunghi spezzati in più parti (decodifica PDU corretta).

## Per chi è pensato

- Chi ha un **PC Windows 11 (o 10) con modem 4G/LTE** e SIM inserita  
- Chi riceve **OTP, notifiche operatore, SMS di servizio** sul modem del laptop  
- Chi non riesce più a usare **Fibocom / Dell Mobile Manager**  
- Chi cerca: *sms modem 4G windows 11*, *leggere sms scheda cellulare pc*, *wwan sms laptop*

## Download

Scarica l’eseguibile dalla pagina **[Releases](https://github.com/pannox/wwan-sms-manager/releases)**:

**→ [`WwanSmsManager.exe`](https://github.com/pannox/wwan-sms-manager/releases/latest)** (portable)

1. Scarica e avvia  
2. Verifica che Windows veda la rete cellulare (`Impostazioni → Rete e Internet → Cellulare`)  
3. Premi **Aggiorna** per caricare gli SMS  

## Requisiti

- Windows 10 o **Windows 11**
- Modem **WWAN / Mobile Broadband** (scheda 4G integrata o USB) con SIM riconosciuta da Windows
- Nessuna installazione di .NET aggiuntiva (usa componenti già presenti in Windows)

## Build dal codice

```powershell
.\build.ps1
```

Genera `release\WwanSmsManager.exe`.

Dettagli di compilazione manuale: vedi commenti in `build.ps1`.

## Hardware compatibile

| Modem | Stato |
|-------|--------|
| Dell DW5820e / Fibocom + Intel XMM7360 | Testato |
| Altri modem MBIM WWAN (Fibocom, Quectel, Sierra, …) | Previsto funzionante se Windows espone gli SMS |

## Perché esiste questo progetto

Su Windows 11 le schede 4G integrate funzionano per i **dati**, ma gli **SMS** restano inaccessibili dall’interfaccia di sistema.  
Gli strumenti vendor chiedono spesso .NET 3.5 o porte AT bloccate dal driver WWAN.  
Questa app parla direttamente con Windows (`Windows.Devices.Sms` / Mobile Broadband) e offre un’interfaccia semplice.

## Licenza

MIT — vedi [LICENSE](LICENSE).

## Avvertenza

Usare solo su dispositivi e SIM di cui si dispone. Gli SMS possono contenere dati personali: gestisci con attenzione eventuali esportazioni.
