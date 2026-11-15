# AIOps MCP Sunucusu (Ajansız)

Kurumsal Windows filosu (~500 sunucu) için ajansız AIOps aracı. LLM'in (vLLM/Qwen, ileride Claude) tool-calling ile canlı tanılama yapmasını sağlayan bir MCP sunucusu. Filoya hiçbir yazılım kurulmaz; tüm erişim WinRM + JEA üzerinden, tamamen salt-okurdur.

## Mimari (değişmez çerçeve)

```
Kullanıcı/SCOM alarmı → LLM → MCP sunucusu (bu repo) → JEA/WinRM → filo
                                    ↘ MS SQL (envanter snapshot) + Qdrant (filo araması)
```

- Merkezde **kalıcı olay/log verisi tutulmaz.** Tek kalıcı veri: sunucu başına ~100 KB envanter snapshot'ı (upsert, geçmişsiz).
- Alarm üretimi SCOM'un işidir; bu sistem **reaktif** çalışır (soru/alarm gelmeden filoya paket gitmez). Tek istisna: gece envanter turu.
- Filo tarafındaki tek gereksinim SCCM (veya GPO) ile dağıtılan JEA endpoint. "Client'a kurulum gerekir" sonucu doğuran hiçbir tasarım kararı alma.
- **Domain controller'lar kapsam dışıdır.** JEA virtual account DC'de domain yönetici ayrıcalığıyla çalışır; bu yüzden DC'lere endpoint kurulmaz (register/SCCM scriptleri DC'yi reddeder) ve `resolve_servers` DC'leri döndürmez (LDAP `userAccountControl` 8192 ve RODC için 67108864 hariç; RODC 8192 taşımaz).
- **Firewall ağ katmanında (firewall cihazları) yönetilir.** Hiçbir script veya kurulum adımı Windows Firewall kuralı oluşturmaz/değiştirmez (`New-NetFirewallRule` yasak). Gerekli akışlar ağ ekibine liste olarak verilir (`deploy/README.md` §6).

## Teknoloji yığını

- .NET 10 / C# 14, resmi `ModelContextProtocol` NuGet paketi (C# MCP SDK)
- Transport: HTTP (streamable). stdio yalnızca lokal geliştirme/debug için.
- Uzak erişim: `System.Management.Automation` (PowerShell SDK) + WSMan bağlantıları, JEA endpoint adı: `AIOpsReader`
- Kimlik: gMSA (koda hiçbir credential yazılmaz; bağlantılar servis kimliğiyle kurulur)
- Depolama: MS SQL Server (`Microsoft.Data.SqlClient`, bağlantı entegre kimlik/gMSA ile — connection string'de kullanıcı/şifre olmaz), Qdrant (envanter özet embed'leri, bge-m3)
- Loglama: Serilog, structured; her tool çağrısı (hedef, parametreler, süre, sonuç boyutu) loglanır
- MCP ucunda kimlik doğrulaması yok (tasarım gereği; config'e secret yazılmaz). Uygulama düzeyindeki tek denetim `Mcp:AllowedClientIps`'tir — boşsa 5100'e erişen herkes filoya okuma erişimi kazanır ve açılışta uyarı loglanır. Webhook'un kendi listesi ayrıdır (`Alerts:AllowedSourceIps`).

## Depo yapısı

```
src/
  AiOps.Mcp/              # MCP host + tool tanımları
    Tools/                # ResolveServers, GetEvents, GetCounters, GetInventory, SearchInventory, RunReadonly
    Remoting/             # PSSession havuzu, paralel fan-out, WSMan bağlantı yönetimi
    Inventory/            # toplama, MS SQL upsert (MERGE), Qdrant embed
    Db/                   # şema migration scriptleri (idempotent .sql, DbUp ile)
  AiOps.Contracts/        # tool girdi/çıktı DTO'ları
build.ps1                 # test + self-contained publish + zip (artifacts/AiOpsMcp-<sürüm>.zip)
deploy/
  README.md               # Quick start (8 adım) + referans; ağ akış tablosu §6 (tek doküman)
  Test-AiOpsPrerequisites.ps1  # MCP host: salt-okur ön kontrol (PASS/WARN/FAIL)
  Install-AiOpsMcp.ps1    # MCP host: Windows Service kurulumu/yükseltmesi (gMSA, config birleştirir)
  Uninstall-AiOpsMcp.ps1  # MCP host: servisi kaldırır
  appsettings.Production.example.json  # ortam ayarı şablonu (gerçeği repoya girmez)
  jea/                    # AIOpsReader.psrc/.pssc, Register scripti; Install-JeaEndpoint.ps1 = SCCM'siz dağıtım
  sccm/                   # Configuration Baseline tespit + düzeltme scriptleri
  sql/                    # CreateDatabase.sql (collation sabit)
tests/
  AiOps.Mcp.Tests/        # birim testleri (remoting mock'lu)
  AiOps.Mcp.LabTests/     # lab ortamına karşı entegrasyon testleri (CI'da koşmaz)
```

## Komutlar

```bash
dotnet build
dotnet test tests/AiOps.Mcp.Tests          # hızlı, mock'lu; her değişiklikten sonra koş
dotnet run --project src/AiOps.Mcp          # lokalde stdio modda başlatır
dotnet run --project src/AiOps.Mcp -- --http # HTTP transport ile başlatır
```

Lab entegrasyon testleri (`AiOps.Mcp.LabTests`) gerçek sunucu ister; kendiliğinden çalıştırma, ben istediğimde çalıştırılır.

Depo public'tir: gerçek sunucu/domain adı, IP, bağlantı dizesi veya ortam config'i commit edilmez. Örneklerde `contoso.local` ve `<yer tutucu>` kullanılır; `appsettings.Production.json` ve `.claude/` `.gitignore`'dadır.

## Güvenlik değişmezleri (asla ihlal etme)

1. **Güvenlik sınırı JEA'dır, MCP kodu değil.** MCP tarafında komut metnini string/regex ile filtreleyip "güvenli" sayan hiçbir mekanizma yazma (`StartsWith("Get-")` vb. yasak). `run_readonly` komutu olduğu gibi JEA endpoint'ine gönderir; neyin çalışabileceğine `AIOpsReader.psrc` karar verir.
2. JEA yapılandırması: `SessionType = RestrictedRemoteServer`, `LanguageMode = NoLanguage`, `TranscriptDirectory` zorunlu. `VisibleCmdlets` listesine yazma/değiştirme yapabilen hiçbir cmdlet ekleme (`Set-*`, `New-*`, `Remove-*`, `Invoke-Expression`, `Start-Process` yasak). Listeye ekleme önerin varsa yapmadan önce bana sor — bu, otonom ilerleme kuralının istisnasıdır.
   **Her girdi parametre izin listesi taşır.** Çıplak string bir girdi o cmdlet'in *tüm* parametrelerine izin verir; rol bu yüzden bir dönem keyfi registry okumasına (`-Path` ile Winlogon, servis `ImagePath`, SNMP community), ham Security log dökümüne (`-LogName`) ve başka hosta yönlenmeye (`-ComputerName`, `-CimSession`) açıktı. İzin listesine parametre eklemek de ekleme sayılır: önce bana sor. `-Path` desenini genişletmek özellikle hassastır, çünkü sanal hesap yerel yöneticidir ve registry kimlik bilgisi barındırır.
3. Koda, config'e veya test dosyalarına credential/secret yazılmaz. Bağlantılar gMSA/entegre kimlikle kurulur.
4. Tool çıktılarına ham PII sızdırma eğilimini sınırla: event mesajları kırpılır, `max_events`/`max_rows` üst sınırları zorunludur ve kaldırılamaz.

## Performans kuralları

- **Filtre her zaman hedef sunucuda çalışır.** `get_events` = `Get-WinEvent -FilterXml` (asla tüm log çekip client'ta `Where-Object`). FilterXml üretimi `Tools/GetEvents` içinde tek yerden yapılır.
- PSSession havuzu: sunucu başına en fazla 1 kalıcı oturum, LRU ile en fazla 100 oturum açık tutulur, kopan oturum bir sonraki çağrıda şeffaf yenilenir.
- Çoklu sunucu çağrıları runspace pool ile paraleldir; varsayılan eşzamanlılık 20, sunucu başına timeout 30 sn, timeout'a takılan sunucu sonucu `unreachable` olarak döner (tüm çağrıyı düşürmez).
- LLM context'i korunur: `get_events` varsayılan `max_events=200`, mesaj gövdesi 500 karaktere kırpılır; `get_counters` özet istatistik (min/max/avg/son) döner, ham örnekler yalnızca istenirse.

## Bilinen tuzaklar

- **`Win32_Product` sınıfı yasaktır** (sorgu MSI reconfigure tetikler). Kurulu programlar registry `Uninstall` anahtarlarından okunur (HKLM 64-bit + WOW6432Node).
- Türkçe locale'li sunucularda performans sayacı adları farklıdır. Sayaç çözümlemesi ada değil **PDH counter index'ine** göre yapılır; İngilizce ad ↔ index eşleme tablosu `Remoting/CounterCatalog.cs`'te tutulur.
- Filo çoğunlukla Windows PowerShell 5.1; JEA ve uzak komutlar 5.1 uyumlu olmalı (PS7-özgü sözdizimi uzak tarafa gönderilmez). MCP sunucusunun kendi kodu .NET 10'dur, karışmaz.
- WinRM kotaları (`MaxConcurrentOperationsPerUser`, `MaxMemoryPerShellMB`) varsayılanda düşüktür; fan-out eşzamanlılığı bu kotaları aşacak şekilde artırılmaz.
- Dinlenen portlar `Get-NetTCPConnection -State Listen` + owning process eşleşmesiyle alınır, `netstat` parse edilmez.
- **JEA parametre izin listesi üç tuzak taşır.** (a) Kısıtlı girdi proxy fonksiyon olarak yayımlanır ve **yalnızca modül nitelikli** adla görünür; çıplak ad kaybolur, kod nitelikli göndermezse tüm filoda "is not recognized" alır. (b) Proxy ancak modül oturumda yüklüyse üretilebilir; `NetTCPIP`, `ScheduledTasks` ve `CimCmdlets` `RestrictedRemoteServer` oturumuna gelmez, bu yüzden `ModulesToImport` şart — yoksa girdi sessizce hiçbir komut üretmez. (c) `ErrorAction` ortak parametredir; izin listesine yazılırsa "defined multiple times" hatasıyla **rolün tamamı** yüklenmez.
- **SCOM zaman damgaları UTC'dir ama `Kind=Unspecified` gelir.** Örtük `DateTime`→`DateTimeOffset` dönüşümü bunları yerel sayıp host offsetini yapıştırır; sunucu tarafı pencere `[datetime]::UtcNow` ile kurulduğu için iki taraf farklı çerçevede kalır. `ScomTools.ToOffset` ve `ParseOffset` bunu tek yerden UTC'ye sabitler; psrc tarafında her damga `[datetime]::SpecifyKind(...,'Utc').ToString('o')` ile yazılır.
- MS SQL tarafında Türkçe collation tuzağı: instance collation'ına güvenme, envanter DB'si açıkça `SQL_Latin1_General_CP1_CI_AS` ile oluşturulur (Türkçe 'I/ı' büyük-küçük dönüşümü sunucu adı/servis adı karşılaştırmalarını kırar). String karşılaştırmaları DB collation'ına bırakılır, kodda `ToUpper`/`ToLower` ile normalizasyon yapılacaksa `ToUpperInvariant` kullanılır.

## Tool sözleşmeleri (imzalar sabit, davranış Contracts'ta)

- `resolve_servers(filter)` — AD'den OU/isim desenine göre sunucu listesi
- `get_events(servers[], channel, event_ids?, level?, start, end, xpath?, max_events=200)`
- `get_counters(servers[], counter_paths[], samples=5, interval=1, include_samples=false, max_series=50)` — bir wildcard yol örnek başına bir seri açar; tavan 200, ham örnek istenirse 25
- `get_inventory(server, sections[])` — sections: services|software|ports|gpo|hotfix|certs|tasks
- `search_inventory(query)` — Qdrant üzerinden filo genelinde anlamsal arama
- `run_readonly(server, command, parameters)` — JEA endpoint'inde çalışır. Cmdlet adı **modül nitelikli** olmalı (`Microsoft.PowerShell.Management\Get-Service`): parametre kısıtı taşıyan bir JEA girdisi yalnızca nitelikli adla yayımlanır, çıplak ad bulunamaz
- `get_scom_alerts(severity?, resolution_state=0, monitoring_object?, max_alerts=100)` — açık alarmlar
- `get_scom_health(object_name, max_objects=100)` — izlenen nesnelerin sağlık durumu (DC'ler dahil)
- `get_scom_performance(object_name, counter?, hours=24, max_series=100, include_samples=false)` — SCOM'un topladığı geçmiş sayaçlar, min/avg/max/last özetli (DC'ler ve AD'ye özgü sayaçlar dahil)
- `get_scom_inventory(computer_name, max_computers=50)` — keşif verisi: DNS/IP/OU/forest/VM/OS, diskler, NIC'ler, DC'lerde FSMO rolleri
- `get_scom_alert_detail(alert_id)` — alarmın kök neden zinciri: monitor/rule, hedef sınıf, management pack, knowledge article, yürürlükteki override'lar, çözüm geçmişi
- `get_scom_state_changes(object_name, hours=24, only_unhealthy=false, max_changes=100)` — monitor sağlık geçiş zaman çizelgesi
- `get_scom_monitoring_health()` — izleme sisteminin kendi sağlığı: grey ajanlar, heartbeat, bakım modu, yönetim sunucuları, saklama süreleri, ajansız keşfedilen bilgisayarlar

SCOM tool'larının tamamı yönetim sunucusundaki `AIOpsScomReader` JEA ucundan, gMSA'nın SCOM
Read-Only Operator rolüyle okunur. Filo JEA ucunun erişemediği hostlar (DC'ler) yalnız buradan okunur.

**Read-Only Operator yönetim verisini okuyamaz.** `Get-SCOMAgent`, `Get-SCOMManagementServer`, heartbeat/grooming ayarları, pending ajanlar ve bildirim abonelikleri SCOM Administrator ister. `get_scom_monitoring_health` bunları dener, reddedileni `unavailable` listesinde adıyla bildirir, durumu `partial` yapar, ayarları `null` (asla 0) döndürür; ajan ve yönetim sunucusu listesi izleme sınıfı örneklerinden (ad + sağlık) gelir. SCOM wrapper'ında `-ErrorAction SilentlyContinue` ile yetki reddini yutma: sonuç `ok` + sıfırlar olur ve yalnızca SCOM admin olmayan bir hesapla test edince görünür. gMSA'ya SCOM Administrator verme.

İmza değişikliği gerekiyorsa önce `AiOps.Contracts`'ı güncelle, testleri uyarla, sonra implementasyon.

## Çalışma tarzım / beklentiler

- Otonom ilerle; her adımda onay isteme. Güvenlik değişmezlerine dokunan kararlar (madde 2'deki gibi) tek istisna.
- Açıklamaları kısa tut; uzun özet paragrafları yazma. Kod ve test konuşsun.
- Bir işi bitirince `dotnet build` + birim testleri koşmadan tamamlandı sayma.
- Yorumlar ve commit mesajları İngilizce, benimle iletişim Türkçe.

## Yol haritası (sıra önemli)

1. JEA çifti (`deploy/jea`) + MCP iskeleti + `get_events` uçtan uca
2. Session havuzu + paralel fan-out + `get_counters`
3. `get_inventory` + MS SQL şeması/upsert + gece turu
4. `search_inventory` (Qdrant + bge-m3)
5. `run_readonly` + `resolve_servers`
6. SCOM webhook entegrasyonu (ayrı servis, bu repoda sadece HTTP ucu)
