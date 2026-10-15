# PR 0: Designspecifikation för MethodCache före 1.0

Revision 3, 2026-10-14. Historisk gransknings- och prestandabaslinje:
f43854dbcd3646893f490e78a0a1d9651e9c4521. PR 1 startar från main efter att
[PR 122](https://github.com/eoniclabs/MethodCache/pull/122) blivit grön och mergad; exakt start-SHA
registreras i implementations-PR:n. Regressionstesterna från PR 122 ska bevaras.

Detta dokument ersätter designspecifikationen som Johan bifogade i designgranskningen och innehåller
justeringarna från den efterföljande granskningen. Det är målbilden för PR 1–7, inte en beskrivning av
nuvarande API. Historiska fynd och äldre rekommendationer är bakgrund; vid konflikt gäller detta dokument.
API-kontrakten anges som notation i tabeller. Kompilerbara användarexempel ska införas som mdsnippets i
tests/MethodCache.Docs.Tests i respektive implementations-PR.

Ledstång: **lättförståeligt, enkelt, tydligt; ingen reproducerbar prestandaförsämring.**
Korrekthet och isolering är egna godkännandekrav. Att uppdatera detta dokument innebär inte att en release
är publicerad eller att implementationstesterna har körts. Användarens begäran i denna chatt omfattar PR 0;
implementationen av PR 1–7 är nästa arbetssteg och ingår inte i denna dokumentändring.

## 1. Beslut och omfattning

| Område | Bindande beslut |
|---|---|
| Release | Alla ändringar landar före Unshipped → Shipped. En rc kan användas för att verifiera publiceringsvägen. |
| Publikt API | Användar-API och ett litet, tekniskt publikt generator-kontrakt. Övrig implementation är intern. |
| Registrering | AddMethodCache() dekorerar uttryckligen registrerade tjänster. AutoRegister kräver uttrycklig livstid och entydig kandidat. |
| Konfiguration | En MethodCacheBuilder för startup. Runtime-overrides är lokala per container; de sprider ingen policy till andra noder. |
| Policyändringar | Lokal ogiltigförklaring är omedelbar. L2 och andra noders L1 följer garantierna i §5; ingen kvittens från alla noder utlovas. |
| L2-policyidentitet | Gemensam deklarativ konfiguration har en stabil identitet. Lokala runtime-overrides har en privat identitet. |
| Cookies | Cookies stängs av och det loggas. KeepCookies behåller transporten orörd och kringgår response-caching helt. |
| HTTP | Samma interna motorimplementation, transport, administration och telemetri. HTTP äger freshness, revalidation och generationer. |
| HTTP med L2 | Bara L2; inga lokala svarskopior eller lokalt cachade generationsvärden. |
| Minnesbudget | Separata motorinstanser/budgetar: metodresultat i antal eller egna vikter, HTTP i bytes. |
| HTTP-admin med L2 | Ett begränsat lokalt observationsregister. Ingen global inventering av Redis/SQL utlovas. |
| Grupp-Enabled | Ett default. Ett explicit metodvärde vinner oavsett lagrens relativa prioritet, enligt befintlig specificitetsregel. |
| Duration | Positiva s/m/h/d-suffix samt [d.]hh:mm:ss. Rena tal avvisas. FailSafe har uttrycklig avstängning. |
| Telemetri | Meter via IMeterFactory och ActivitySource i Core. Ingen OTel-dekorator eller baggage-propagator. |
| Utbyggare | Backplane, policykällor, nyckelgeneratorer och HTTP-lagring är interna. ICacheKeyProvider och användarens ICacheManager finns kvar. |
| Senare | Tak för samtidiga laddningar (S7) kommer efter 1.0. Global L2-inventering och HTTP-L1 med L2 ingår inte i 1.0. |

Ingen befintlig säkerhets- eller korrekthetsregression får tas bort för att få den nya designen att passa.
En ersatt mekanism ska klara samma observerbara beteende, utom där en förändring uttryckligen anges här.

## 2. Generatorns kontrakt och samtidighet

### 2.1 Stabil bootstrap och versionering

Alla MethodCache-runtimeanrop i genererad kod går genom MethodCache.Generated.
BCL och användarens tjänster, typer, medlemmar och generiska constraints får naturligtvis refereras.
Generatorn får inte anropa policy-pipelinen, lagringsimplementationen, DI-extensioner eller telemetrin direkt.
ICacheKeyProvider-parts går genom en helper i fasaden, så att generatorns runtimeberoende förblir avgränsat.

Typerna är tekniskt publika, märkta EditorBrowsable(Never), XML-dokumenterade som generator-kontrakt och
spårade i PublicAPI. Namespace är inte en åtkomstgräns: signaturer, baser, konstruktorer och returnerade
typer är också kompatibilitetslöften.

- GeneratedCode.Register(contractVersion, module) är den stabila bootstrapen. Module initializer får endast
  skapa en enkel deskriptor och registrera den. Ingen policyupplösning, DI eller exekvering av Describe sker där.
- Register lagrar även okända versioner utan att kasta. AddMethodCache validerar versionerna innan någon
  Describe/factory körs och ger ett fel med assemblynamn, kontraktsversion och instruktionen att uppgradera.
- Garantin om detta fel gäller assemblies som använder den kända bootstrapen. Ny kod som refererar typer eller
  medlemmar som helt saknas i en äldre runtime kan fortfarande nekas av CLR; test med en ändrad siffra bevisar
  inte godtycklig framåtkompatibilitet.
- Kontrakt 1 är en uppsättning stödda medlemmar, inte samma sak som paket- eller wire-version.
  Under 1.x behålls varje utgiven signatur. Tillägg får inte introducera overload-ambiguity för tidigare källkod.
  Nya abstrakta medlemmar på GeneratedModule är förbjudna; framtida virtuella medlemmar måste ha default.
- Package validation mot 1.0.0 och gamla minor-baslinjer kontrollerar binär kompatibilitet.
  Före 1.0 används den frysta PR 1-baslinjen. Det opublicerade gamla kontraktet från f43854d ersätts utan shims.
- GeneratedCode-attributet anger generatorns verkliga version.
- Global registrering innehåller bara modulbeskrivningar. Bindning, policy, generation, service key, scopes och
  metrik tillhör respektive container. Inget val får bero på testhost eller GetEntryAssembly som utslagsregel.
- Två moduler som erbjuder samma interface måste väljas uttryckligen via builderns modulval; annars tydligt fel.
  FromAssemblyOf gör modulval och trim/AOT-rootning uttryckliga. Registreringar får aldrig avgöras alfabetiskt.

### 2.2 En oföränderlig snapshot per anrop

CachedMethod är en bunden metodplats för en viss tjänsteregistrering och container. Flera platser kan dela
samma policyhandtag, men behåller egen registreringsidentitet och mål för bakgrundsscope.

CachedMethod.Capture() gör en volatile-läsning och returnerar ett litet värdeobjekt MethodCall som refererar
en oföränderlig snapshot. Den snapshoten innehåller effektiv policy, Enabled, nyckelprefix, L2-policyidentitet
och lokal generation. Samma MethodCall används för nyckelbygge, uppslag, tags, laddning och publicering.

| Typ/ingång i MethodCache.Generated | Kontrakt |
|---|---|
| GeneratedModule.Describe(builder) | Beskriver dekoratorer, kandidater och attributdata; körs efter versionsvalidering. |
| GeneratedModuleBuilder.Decorator | Tjänstetyp och fabrik som tar inner-instans och registreringsbundna GeneratedServices. |
| GeneratedModuleBuilder.ImplementationCandidate | Namngivningsbar, konkret implementation med DI-åtkomliga konstruktorer; data för AutoRegister. Ingen runtime-typskanning. |
| GeneratedModuleBuilder.Method | Method-id, parameterordning/namn för taggmallar, grupp och alla attributinställningar med skillnad mellan unset och explicit off/false/0. |
| GeneratedServices.Method | Binder metod-id och deklarerande/closed service type till registreringskontexten. Context bär service key och inner-DI-slot. |
| GeneratedServices.InvalidateTagsAsync | Tar tags och caller-token. Samma återhämtningskontrakt som användar-API:t. |
| CachedMethod.Capture | Returnerar MethodCall; inga oberoende läsningar av KeyPrefix/Enabled/policy får göras. |
| MethodCall.StartKey | Tar anroparens stackbuffer och returnerar en KeyBuilder bunden till anropets snapshot. Runtime skriver hela det fasta prefixet och äger gränsen mot argumentdelen. |
| MethodCall.TryGetTask / TryGetValueTask / TryGetValue | Tar ref KeyBuilder eller in TextKey och out-resultat. Samma MethodCall som skapade byggaren används. Ett verkligt hit räknas en gång. False räknar inte automatiskt miss. |
| MethodCall.BindTextKey / TextKey | Binder redan ägd strängtext till samma snapshot. En readonly TextKey bär bara text och bindning, utan oanvänd skrivbar span; samma felbindnings- och stale-regler gäller. |
| MethodCall.LoadAsync / LoadValueAsync | Tar strängnyckel, generiskt state, statisk loader, taggvärden och token. Snapshoten följer laddningen. |
| MethodCall.Load | Verklig synkron exekveringsväg för synkrona metoder; se §4.3. |
| MethodCall.ServiceForLoad | Återanvänder inner-instansen eller löser rätt registrering i ett nytt scope för arbete som outlever anroparen. |
| MethodCall.CreateFallbackKey | Kodar argument utan inline-form genom den interna FastHash-kodaren, med snapshotens prefix/version och registreringsidentitet. Returnerar en bunden KeyBuilder för samma uppslagskontrakt; reservvägens befintliga allokeringar mäts separat. |
| KeyBuilder | Ref struct med AppendPart för befintliga primitiver, records, samlingar och provider-parts. Nyckeltext, snapshotbindning och eventuell processlokal hash hanteras tillsammans; inga hashfält exponeras i generator-kontraktet. |

Exakta C#-signaturer fastställs och kompileras i PR 1 innan kompatibilitetsbaslinjen skapas. De ska uppfylla
tabellen, ha fullständiga nullable-kontrakt och inga läckor av interna typer. De ursprungliga separata
KeyPrefix/Enabled-properties är ersatta av MethodCall, och span-uppslaget av en bunden KeyBuilder; befintlig strängtext använder en bunden TextKey.
Formen får justeras för att uppfylla kontraktet och
prestandagrinden; en ofullständig skiss ska inte låsas som API.

Generatorn fångar först MethodCall, startar sedan byggaren från det anropet, lägger till argument och gör
uppslaget med ref KeyBuilder. StartKey eller uppslaget får inte läsa om CachedMethod för att hämta ett nytt
prefix eller en ny policy. En byggare från en annan metod, container eller snapshot avvisas med tydligt fel
före cacheåtkomst; en rätt bunden men inaktuell snapshot följer bypass/retry-regeln nedan.
Byggaren förbrukas inte av ett missat uppslag: samma nyckeltext kan materialiseras till Load/LoadAsync.
Nyckeln och övrigt nödvändigt state materialiseras innan async-arbete lämnar den synkrona anropsdelen.
Ingen stackbuffer eller ref struct får behållas av en Task, loader, bakgrundsrefresh eller lagrad cachepost.

PR 1 väljer en liten, begränsad stackbuffer utifrån fasta delar och kända argumentformer. Ett stort prefix,
en lång sträng eller oförutsedd provider-part växer säkert till heapen; ingen input bestämmer obegränsad
stackalloc. Skrivning och hashning läser bara initialiserade tecken. Buffertstorleken får inte ändra
nyckeltexten, escaping eller reservvägsvalet. Noll extra allokeringar krävs för motsvarande träffvägar;
befintlig net8-fallback och reservnycklar jämförs mot sin egen baslinje.

Publicering kontrollerar laddningens generation och invalidationsstatus under samma skydd som själva
publiceringen. Att kontrollera generation före en separat skrivning är inte tillräckligt.
Ett äldre anrop får avslutas med sitt resultat, men får inte publicera det i den nya generationen.
En gammal snapshot får inte läsa eller starta en cacheladdning som om den vore aktuell: stale snapshots
kringgår cachen och kör den underliggande metoden, eller upprepas innan loadern startats.

Scopeägande och cancellation ska bevara nuvarande beteende: egen scope för scoped/transient bakgrundsladdning,
rätt keyed inner-instans, ingen användning efter dispose, och ingen implicit HttpContext/AsyncLocal-identitet
i refresh-ahead. Refresh får inte dubbelregistrera eller dubbelavsluta samma anrop i metrik.

### 2.3 Paket och kompatibilitetsbaslinjer

Alla sex tilläggspaket, inklusive MethodCache.Serialization.MessagePack, når interna Core-API:er genom
InternalsVisibleTo och kräver exakt samma MethodCache-paketversion i nuspec, exempelvis intervallet [1.2.3].
Pakettestet installerar olika ordningar och kör strikt restore (NU1608 som error) för blandade tillägg/Core-versioner.
[NuGets direct-dependency-wins](https://learn.microsoft.com/en-us/nuget/concepts/dependency-resolution#direct-dependency-wins)
kan annars överstyra även ett exakt transitivt beroende med en varning;
applikationer får inte undertrycka eller kringgå versionskravet. Stöd för vendor-DLL mot
senare Core är separat från detta krav.

CompatTests kör färdigkompilerade vendor-DLL:er med källkod, full commit/paketversion och SHA-256 i ett manifest.
Baslinjerna får inte regenereras under en vanlig testkörning. PR 1 skapar första baslinjen efter att fasaden
är komplett; 1.0.0 blir första publicerade baslinjen och varje minor lägger till en ny.
Fixtures täcker sync/Task/ValueTask, keyed/scoped tjänster, generiska metoder, reservnycklar, tags och
CacheInterface. Källkod och fixtures som provar den nya fasaden kompileras före baseline-frysningen.
Fixtures inkluderar StartKey/ref KeyBuilder och reservvägen, så att senare prefixhashning kan införas
utan ändrade generator-signaturer eller omkompilering av vendor-DLL:en.

### 2.4 Prefixhashning efter PR 5

En separat prestanda-PR efter PR 5 och före slutverifieringen i PR 7 kan återanvända hashtillståndet efter
snapshotens fasta prefix. PR 1 inför kontraktsformen och buffertvalet; den behöver inte införa den nya
motorjämföraren. Hashstate ligger i den oföränderliga snapshoten eller refererad oföränderlig data, aldrig
som ett separat muterbart prefix på CachedMethod. Generatorn beräknar eller skickar ingen egen hash.

Den interna ordboksjämföraren använder samma hashfunktion och seed för hela strängnycklar, span-nycklar,
reservnycklar och nycklar byggda i delar. För samma seed och samma UTF-16-text måste hashen bli identisk
oavsett delningspunkt, bufferttillväxt eller uppslags-/skriv-/borttagningsväg. Missar kan kräva hashning
av hela texten, men får inte använda den gamla hashfunktionen om den nya jämföraren används.
Equality jämför alltid hela nyckeltexten ordinalt; en hashkollision får aldrig vara en cacheträff.
En ändring efter en beräknad hash måste göra eventuell cachad hash inaktuell.
Detta följer även [BCL-kontraktet för alternativa jämförare](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.ialternateequalitycomparer-2.gethashcode?view=net-10.0).

Hash och seed är intern, processlokal metadata och ingår inte i nyckeltext, L2, wire-format, admin,
MethodCacheKeys.For eller invalidationsmeddelanden. Restart eller en annan process får inte ändra dessa
identiteter. Policy-/versionsbyten skapar prefixstate för den nya snapshoten och behåller dess isolering.

En egen jämförare måste ha dokumenterat skydd mot avsiktliga kollisionsangrepp för det faktiska algoritm-
och seedvalet. Ett slumpat seed och jämn fördelning över slumpdata räcker inte ensamma som säkerhetsbevis.
[BCL:s automatiska omhashning](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Collections.Concurrent/src/System/Collections/Concurrent/ConcurrentDictionary.cs)
för den inbyggda strängjämföraren får inte antas skydda en egen jämförare. Algoritm och hotmodell granskas
innan jämföraren införs; säkerhetstester och docs/security.md uppdateras i samma PR.
Utgångstid, frekvensspårning, eviction, typkontroll, refresh/sliding och träffräkning ska bevaras.

## 3. Registrering, konfiguration och livstid

Publika startup-ingångar är AddMethodCache() och AddMethodCache(configureBuilder), utan valfria parametrar.
MethodCacheBuilder erbjuder Defaults, Group, Method (typat uttryck eller verifierat method-id),
FromConfiguration, FromAssemblyOf, Memory, AutoRegister samt tilläggens UseRedis, UseSqlServer,
UseMessagePack och UseJson. Bekvämlighetsöverlagringar är uttryckliga; de ändrar inte en befintlig signatur.

- Vanlig DI-registrering sker före AddMethodCache. Implementationsinstanser, fabriker, lifetimes, keyed
  tjänster, IEnumerable-ordning och disposalägande bevaras.
- Senare registrering av ett cachebart interface ger fel vid host-start, och vid första bindningen utan host.
  Kontrollen görs per container på registreringarnas identitet, även för fler registreringar av samma interface.
  Andra tjänster får registreras senare. Upprepade AddMethodCache-anrop får varken dubblera dekoratorer eller
  göra en godkänd registrering felaktigt sen.
- AutoRegister(lifetime) är ett uttryckligt tillval för de valda modulerna. Saknas DI-registrering väljs exakt
  en åtkomlig konkret kandidat. Noll kandidater och flera kandidater ger fel med interface, modul och kandidater.
  Redan registrerade tjänster ignorerar kandidatvalet. Samma urval gäller JIT och Native AOT.
- Open generics och KeyedService.AnyKey får inte tyst bli okachade. När en registrering saknar en stödd,
  genererad bindning ska startupfelet ange det; konkreta closed generics och vanliga keyed tjänster stöds.
- Runtime-konfiguratorn erbjuder UpsertAsync, RemoveAsync, ClearAsync och GetOverridesAsync, samt typade
  method-expression-överlagringar. Alla tar token sist. Okänt method-id avvisas utan att lagras.
  Batch/mask/publik CachePolicy tas bort. Grupp/global-defaults vid startup och config finns kvar; runtime-
  konfiguratorn ändrar metoder (ingen dold gruppadministration via en felstavad metodsträng).
- Runtime-overrides ändrar bara den lokala policyn. En invalidationssignal kan skickas till andra noder,
  men betyder aldrig att deras policy ändrats. Appsettings måste distribueras och laddas om av applikationen
  på varje nod; biblioteket distribuerar inte konfigurationsfiler.
- Policy-pipelinen är intern. GetMethods visar effektiv policy, källa per inställning och om L2-namespace är
  shared eller lokal. Enbart ändrad audit-metadata får inte invalidiera värden.
- Specificitet är metod → grupp → defaults. Inom samma specificitet gäller runtime → config → startup → attribut.
  Explicit metod-Enabled=true vinner därför över grupp-default false även från ett lägre lager.
- Tags är fortsatt additiva över lagren om inte ClearTags uttryckligen används.

MethodCacheMemoryOptions behåller MaxItems, MaxHttpBytes, Weigher + SizeLimit, CacheNullValues,
JitterMaxDuration och MaxExpiration. Weigher/SizeLimit gäller bara metodresultat och mäts i användarens
kostnadsenheter; det är inte ett automatiskt byte-estimat. HTTP har alltid separat byte-budget.
DefaultExpiration ersätts av Defaults.WithDuration. Utan explicit default gäller fem minuter.
TinyLFU är enda policy. FastPath/statistics/cleanup-switchar och serialiserande minnesestimat tas bort.
Intern maintenance och automatisk expiry finns kvar, med testbar TimeProvider och fast intern schemaläggning.
MaxExpiration är ett metodtak; det kapar inte HTTP:s freshness- eller stale-retention-regler.

## 4. Användar-API, managers och invalidationsresultat

### 4.1 Fyra metoder

| ICacheManager | Retur och parametrar |
|---|---|
| GetOrCreateAsync | ValueTask med typat resultat; key, factory med CacheContext och token, immutable CacheEntryOptions eller null, caller-token sist. |
| TryGetAsync | ValueTask med CacheLookupResult (Found och Value); key och token. Default/null/false/0 är giltiga hits. |
| InvalidateKeysAsync | ValueTask med InvalidationResult; IEnumerable av logiska nycklar och token. |
| InvalidateTagsAsync | ValueTask med InvalidationResult; IEnumerable av applikationstaggar och token. |

Extensions erbjuder enkla explicit-key-anrop, builder-konfiguration, versionerade uppslag/invalidation,
en tagg/nyckel och GetOrCreateManyAsync. GetOrCreateMany behåller korrekt merge med samtidiga skribenter och
avgränsad L2-parallellism. Logiska versionerade nycklar konstrueras centralt; användaren gissar inte formatet.

En builder gäller startup, config, runtime och manuella anrop: duration, sliding, refresh, fail-safe,
tags/clear-tags, version, Distributed och Enabled. Typade anrop/metoder har CacheWhen.
OnHit/OnMiss/When(CacheContext)/WithMetrics/ICacheMetrics och CacheContext.Services tas bort.
CacheContext behöver inte en IServiceProvider. Publicering av options tar kopior av muterbara samlingar.

Användarens async-API har token sist. Generatorfasaden har också tokens på async-laddning/invalidation;
bootstrap/Describe och rena synkrona helpers omfattas inte av reflection-regeln.

### 4.2 Partiella batcher

InvalidationResult är en immutable resulttyp, inte det tidigare flags-enumet. Följande räknare ingår:

| Fält | Betydelse |
|---|---|
| Requested | Antal distinkta validerade targets (keys eller tags). Dubbletter räknas en gång. |
| Local | Targets vars lokala poster/laddningar är ogiltiga. Det kan vara en no-op utan befintliga poster. |
| Distributed | Targets vars delade invalidationsmetadata är bekräftade av L2. |
| Queued | Targets vars L2-invalidation ännu inte bekräftats och lagts i återhämtningskön. |
| Published | Targets för vilka backplanen bekräftat att meddelandet skickats; inte mottagits av alla noder. |
| PublicationQueued | Targets med bekräftad L2-invalidation men misslyckad backplane-publicering som ska återförsökas. |

Räknarna har skilda betydelser och får inte summeras till Requested. Med L2 konfigurerad gäller
Distributed + Queued = Requested vid normalt avslut. Published och PublicationQueued är högst Distributed
och är disjunkta när en backplane finns. Utan L2 är dessa räknare noll; utan backplane är Published och
PublicationQueued noll. Tom batch ger nollor. Invalid input avvisas innan första mutation.

Efter första mutation avbryter token endast anroparens väntan. Återhämtning körs vidare och eventuella fel
observeras och rapporteras. Före mutation får cancellation lämna allt orört. Ett avbrutet await ger ingen
falsk resultbekräftelse; hälsorapport och kö visar det fortsatta arbetet.
Normal köning är ingen exception för metodcaching. HTTP:s starka invalidation skiljs ut i §9.

### 4.3 Egen manager, NoOp och synkrona metoder

Built-in manager används via en intern adapter som tillhandahåller generatorns optimeringar.
En egen ICacheManager får inte kringgås genom att fasaden läser en separat built-in cache.

- För async-genererade metoder delegerar adaptern till den registrerade managerns fyra metoder.
  Utan intern fast-path-capability tar den vanliga laddningsvägen; användaren implementerar inga nio
  generatorhelpers. Begärd version, tags, Enabled och Distributed följer som publika options.
- NoOpCacheManager kör alltid implementationen och gör ingen L1/L2-publicering. Det gäller genererade
  metoder och manuella anrop. Den påverkar inte HTTP-lagringen; HTTP kringgås med klientens egna options.
- En egen manager har inget publikt synkront kontrakt. Synkrona genererade metoder kringgår då caching
  och använder implementationen direkt, med en engångsdiagnostik per metod/container.
- Built-in sync-metoder har riktig synkron factory och synkrona, deadline-begränsade provideroperationer.
  Ingen Task.Result, Wait, GetAwaiter().GetResult eller motsvarande väntan på en async-loader får användas.
  Om en provider/anslutning inte kan erbjuda en säker begränsad synkron operation behandlas L2 som
  otillgänglig för det anropet, med L1 och implementation som reserv; den synkrona vägen får inte starta
  en obegränsad arbetstråd och vänta på den. Async-factory för en lånad Redis-anslutning väntas aldrig in
  synkront. Detta rapporteras i diagnostik och dokumenteras.
- Single-flight gäller samtidiga foreground-laddningar inom respektive synkron/asynkron exekveringsväg.
  En sync-miss väntar inte på en asynkron refresh; den får utföra en egen sync-laddning med samma
  generations/publiceringsskydd. Async-joiners kan invänta en sync-flight utan att blockera tråden.
- En sync-laddning vars L2-skrivning kan utföras bounded/nativt slutför den före retur. Om den inte kan
  utföras gäller samma fel-/invalidationsåterhämtning som vid otillgänglig L2; detta är ingen dold
  fire-and-forget-skrivning av resultatet.

## 5. Policyändringar, policyisolering och garantier

### 5.1 Var och när en ändring gäller

En ändring av effektiv beteendepolicy publicerar en ny lokal snapshot/generation före retur från
runtime-konfiguratorn (eller före config-reload-notifieringens avslut). Anrop som påbörjar sitt
cacheuppslag därefter kan inte läsa eller publicera i den gamla generationen. Redan startade anrop får
avslutas med sitt tidigare resultat. Upsert med samma effektiva policy/audit-only ändring är en no-op.

| Inställning | Beteende efter ändringen |
|---|---|
| Duration, SlidingExpiration, RefreshAhead, FailSafe och timeout/retry | Ny policy; tidigare lokal generation blir ogiltig. |
| Tags/ClearTags | Ny tagguppsättning; gamla poster ogiltiga så att ny invalidation når nya poster. |
| Version | Ny logisk nyckel och generation; tidigare nycklar nås inte via metoden. |
| Distributed | False kringgår både L2-läsning och L2-skrivning; ändringen gör gamla lokala poster ogiltiga. |
| CacheWhen | Nytt typed predicate; gamla lokala poster ogiltiga. Runtime-förändring får privat L2-identitet. |
| Group | Ny grupptillhörighet/defaults; invalidiera när effektiv beteendepolicy eller gruppidentitet ändras. |
| Enabled=false | Inga cacheläsningar, cacheladdnings-joiner, cachepubliceringar, stale-reserver eller refresh. Metoden körs per anrop. |
| Enabled=true igen | Ny lokal generation och ny identitet för en lokal override; gamla disabled-era värden återkommer inte. |
| Audit/provenance-metadata | Bara diagnostiken ändras; inget cachebortfall om beteendet är identiskt. |

CacheInvalidate och manuella invalidationer fungerar även när en metod är avstängd.

För metodcaching är spridning **best effort**: lokal ändring är omedelbar, L2-tombstone gäller när den
lagrats och andra noders L1 påverkas när backplanen levererar. Vid förlust/frånkoppling kan deras gamla L1
leva till expiry enligt den egna policyn; provider-reconnect rensar lokal cache som i dag.
L2-läsningar kan också fördröjas av den dokumenterade TagCheckInterval. Ingen global barrier utlovas.
Köning och publikationskö syns i health check. En fjärrsignal ändrar aldrig mottagarens policy.

### 5.2 Stabil shared-identitet och privata overrides

En metodpost har separat **logisk nyckel**, **policyidentitet** och **postformat**.
Användarens MethodCacheKeys och InvalidateKeys arbetar på logisk nyckel utan de interna två senare delarna.

- Vid startup/config byggs en shared-policyidentitet av en versionerad, invariant och längdframad
  representation av effektiv duration, sliding, refresh, fail-safe/timeout/retry, version, Enabled,
  Distributed, grupp och ordinalt sorterade distinkta taggmallar. Identiteten innehåller inga owner/ticket,
  timestamps, registration-order, delegate-adresser eller processlokala räknare.
  Samma effektiva deklarativa inställningar och samma tjänste-/service-key-identitet kan dela L2.
- En beteendeändrande lokal runtime-override får en privat, slumpmässig 128-bit namespace-identitet per
  berörd metod och container. Varje efterföljande beteendeändring får en ny identitet, inklusive av/på.
  Två noder med lika lokala overrides delar inte L2; restart återanvänder aldrig en privat identitet.
- När override tas bort återgår metoden till shared-identiteten för sin dåvarande startup/config.
  En ny lokal generation och en logisk metodinvalidation måste fortfarande genomföras; lokal L2-
  återläsning tillåts inte innan den bekräftats, även om shared-identiteten fanns före overriden.
- Appsettings-reload väljer identiteten för den nya effektiva konfigurationen. En nod som ännu inte laddat
  om kan skriva i den gamla identiteten, men kan inte återfylla den uppdaterade nodens namespace.
- Startup-predicates kan inte identifieras automatiskt genom delegates. CacheWhen kontrolleras också
  på L2-resultat före användning; ändrat kodbeteende/resultatschema vid deployment kräver fortsatt
  användarens Version. Runtime-ändrat predicate omfattas av privat override-identitet.
- Shared cache reuse över deployments med samma identitet är avsiktlig. Startkod/attributändring som
  ändrar identiteten ger missar; ändringar som inte representeras (kod/resultatschema) kräver Version.

### 5.3 Metadata och L2-inläsning

Intern metodinvalidation har en typad identitet för method-id, skild från applikationstaggar.
Text som börjar med U+0000 eller imiterar en intern identitet får inte styra metodgenerationer eller ClearAll.
Backplanen använder en skild logisk meddelandetyp för metodinvalidation, definierad innan wire v1 fryses.
Den omfattar alla closed generics, service keys och policyidentiteter för det deklarerade method-id:t.

L2-posten bär LoadStartedAt (för invalidation), CreatedAt (första publiceringen av det färdiga värdet)
och absoluta logiska/fysiska deadlines. CreatedAt flyttas aldrig fram vid transport-retry eller L2→L1.
Vid inläsning måste mottagaren:
kontrollera identitet och invalidation, tillämpa aktuell CacheWhen och aldrig starta om livstiden från
inläsningstidpunkten. Kvarvarande lokal duration får inte överstiga någon av postens deadline,
mottagarens duration från CreatedAt eller mottagarens MaxExpiration. Null-hit avvisas när mottagarens
CacheNullValues är false. En lång factory minskar metadataretentionens marginal från LoadStartedAt men
förbrukar inte i sig Duration, som fortfarande räknas från publiceringen av det färdiga värdet.
Sliding/refresh/fail-safe får inte förlänga posten utöver dess absoluta säkerhetsgränser.

Laddningen bär snapshot, lokal generation och invalidationsidentitet från före källäsningen.
Publicering till L1 är atomisk mot lokal invalidation. En sen L2-skrivning kan inte bli läsbar som ny
bara för att dess transport avslutas efter invalidationen; läsaren validerar LoadStartedAt/tombstone.
Metodstamp och metadataretention måste täcka hela värdets fysiska livstid, load/write-delay och clock skew.
Vid osäkerhet är det en miss, aldrig en förlängd gammal post.

## 6. Nycklar, attribut och duration-parser

- FastHash är enda interna reservkodaren. Prefix/version/registreringsidentitet ägs centralt.
  ICacheKeyProvider finns kvar; raw-key-flaggan och den tidigare no-op CacheKey-attributformen tas bort.
- Nyckelidentitet bevarar typ, parametergränser, closed generic arguments, service key, overload-id,
  exakta UTF-16-kodpunkter och kulturinvariant värdeformat. Inline och reservväg får inte aliasa andra anrop.
- Taggmallar för cache och CacheInvalidate använder samma Part-format som motsvarande värde i nyckeln,
  inklusive Guid, DateTime/offset, enum, null och escape. Egen literaltext för tags ändras inte.
- Attributen är sealed med namngiven Group och parameterlös konstruktor. Enabled/Distributed anges som
  bool-attributproperties, men generatorn använder förekomst i named arguments för att skilja unset från false.
  Version är en int-attributproperty där frånvaro är unset; explicit negativt värde är alltid ett fel.
  Nullable används i den interna datamodellen, inte som en otillåten nullable attributparameter.
- Samma parser används i generator, analyzer och config. Grammatik: invariant positivt decimaltal med ett
  enda suffix s/m/h/d (exempelvis 1.5h), eller invariant TimeSpan-format [d.]hh:mm:ss med valfri sekundfraktion.
  Whitespace runt värdet tillåts, suffix case-insensitive; multipla suffix, exponent, komma, NaN/infinity,
  overflow, negativa värden och ospecificerade rena tal avvisas. Upplösning är ticks; positivt värde som
  avrundas till noll avvisas. Även runtime/builders validerar positiva tidsfönster.
- FailSafe är undantaget: text off (case-insensitive) stänger av; WithFailSafe(TimeSpan.Zero) är kodformen.
  Zero är annars ogiltigt för duration, sliding, refresh, fail-safe-timeout och retry. FailSafeRetryAfter
  behåller default 30 sekunder. Unset följer defaults och är inte off.
- Generatorn rapporterar fel själv och skriver aldrig en default som reserv för ogiltig explicit data.
- Diagnostik får en stabil serie MC0001… och kategorier Usage/Design/Performance.
  Sammanhängande numrering krävs bara vid första frysningen, inte om framtida diagnostik tas bort.
  Gamla→nya id-tabellen och analyzer-release-filer uppdateras i PR 3.

## 7. Telemetri, statistik och health check

Core registrerar IMeterFactory om ingen finns och skapar Meter och ActivitySource med namnet MethodCache.
Meter använder containerns factory och source-livstid är bunden/hanterad så att två containrar inte
delar mutable callback-state. Version kommer från assemblyn. OTel-tillägget erbjuder bara
AddMethodCacheInstrumentation för AddMeter/AddSource. RecordCacheKeys och RecordCacheTags konfigureras
i Core; standard false. Hashed keys är standard när nyckelregistrering aktiveras.

| Instrument | Typ/enhet | Dimensioner och betydelse |
|---|---|---|
| methodcache.requests | Counter, {request} | kind=method/http, name=method-id/client, result=hit/miss/stale/revalidated/bypass/error. Exakt en slutkategori per anrop. |
| methodcache.load.duration | Histogram, s | kind/name, error.type; bara faktiskt körda laddningar från datakällan, inte flight-joiners eller rena L2-hits. |
| methodcache.entries | ObservableUpDownCounter, {entry} | kind; bara faktiskt lokalt lagrade svar/resultat, inte observationsregistret eller uppskattad global L2. |
| methodcache.http.size | ObservableUpDownCounter, By | Lokalt lagrade HTTP-bytes enligt §9.1. Noll body-storage i L2-läge; observerad L2-storlek läggs inte till. |
| methodcache.invalidations | Counter, {invalidation} | type=key/tag/method, stage=local/distributed/queued/published/publication_queued; räknar targets per stage från §4.2. Stages summeras inte till operationsantal. |
| methodcache.distributed.duration | Histogram, s | operation=get/set/remove/invalidate, error.type; individuella transportoperationer. |
| methodcache.distributed.queue | ObservableUpDownCounter, {invalidation} | stage=storage/publication; distinct pending targets. |

En failed fast probe räknar inte miss om senare väg faktiskt ger hit eller joins; refresh har separat
load-duration och är inte en andra användar-request. Exception/stale-fallback avslutar requesten en gång.
URL, key, tags, användar-id och tenant-id får aldrig vara metrikdimensioner.

Spans finns för load, distribuerad operation och invalidation, inte rena träffar. Nycklar/tags endast opt-in;
ingen baggage-propagator eller kopiering av requestens HTTP/url-attribut. Console/Null metrics providers,
ICacheMetricsProvider, ICacheMetrics, TelemetryCacheManager och tidigare OTel-registrering tas bort.
API för instrumentnamn begränsas till MethodCacheTelemetry.Name.

Stats skiljer LocalMethodEntries, LocalHttpEntries, LocalHttpBytes och ObservedDistributedEntries.
Generisk vikt redovisas som TotalWeight i användarens enheter; inget falskt exakt byte-estimat för metodobjekt.
Hit-ratio kan beräknas av app/dashboard. Alla statistikfält anger lokal scope.

Health check ger Healthy utan kö/frånkoppling, och registrerad failureStatus vid cachedegradering
(default Degraded). Rapporten innehåller L2-tillgänglighet, storage/publication queue-längder och
BackplaneStatus=not_configured/connecting/connected/disconnected/unknown, utan identiteter eller värden.
Okänd eller ej etablerad konfigurerad backplane är degraderad. Ett lyckat L2-health-get bevisar inte
backplanehälsa. NoOp/eget manager-stöd redovisas som capability, inte falska built-in cachevärden.

## 8. Distribuerad lagring och wire-format

### 8.1 Logiska nycklar över flera postformat

Värdets fysiska namespace innehåller postformat och policyidentitet (§5.2). Invalidationens metadata har
separata logiska key/tag/method/clear-identiteter och innehåller **ingen** postformat- eller policyversion.
Prefixet för metadata och typen av identity är separata från användartext; UTF-16 bevaras för hashing/framing.

Varje L2-läsning, även av en post som fanns före borttagningen, måste kontrollera den logiska key-tombstonen.
Enbart att kontrollera efter en skrivning eller att ta bort den egna eN-posten räcker inte.
Giltig key-invalidation når alla eN/eN+1-poster och alla shared/private policyvarianter av samma logiska nyckel,
utan enumeration av okända framtida format. Den lokala noden avvisar genast sina äldre poster/laddningar.

En key-tombstone måste leva lika länge som metadataretentionen, inte den gamla femminutersgränsen.
Alla L2-värdens absoluta fysiska deadlines begränsas av metadataretention minus skew/delay från load-start.
Atomiska maximum-writes får aldrig korta retention. Ett schema/wire-kontrakt anger en gemensam
minimiretention för noder som delar scope; det får inte sänkas medan äldre värden fortfarande lever.
Permanent HTTP-generationmetadata har eget kontrakt i §9.2 och använder inte expirerande metodstamps.

Late-write recovery och checksums behandlas enligt nuvarande tillgänglighetskontrakt: ingen garanti om
autentiska värden från en komprometterad store. Bytes som inte kan valideras/läsas blir missar.
Någon ny stark global CAS-garanti för metodcaching införs inte i denna plan.

### 8.2 Ett stabilt invalidationsprotokoll

Frys wire v1 för hela 1.x innan första release: key, applikationstag, intern method och ClearAll är egna
operationer. Version, length och flags är framade, okända valfria fält ignoreras, och längder kontrolleras
före allokering. Okänd obligatorisk operation eller oläsbar ram ger lokal ClearAll och engångsvarning.
Denna reserv reparerar inte meddelanden som aldrig levererats.

Postformat kan ändras i minor och ingår i värdets fysiska nyckel. Backplane/metadata/generationer är
formatoberoende. N och N+1 måste invalidiera varandras redan lagrade poster även vid exakta key-removals.
Headern innehåller LoadStartedAt, CreatedAt, logisk och fysisk deadline samt bounded flags/extension-length.
Serializer-formatidentitet är separat; ändrade serializerinställningar eller resultatkod kräver Version
eller uttrycklig ny formatidentitet. ContentType ensam är ingen schema-garanti.

### 8.3 Providers, options och serialisering

IBackplane, dess meddelanden, atomic-store-kontrakt och serializerregistrering är interna.
Owned tjänster har samma interna DI-key. En enda startup-immutable KeyPrefix på DistributedCacheOptions
bestämmer värden, stamps, HTTP-generationer och backplane-kanal/SQL-scope. KeyPrefix från alla Configure-
registreringar läses vid bindning/start, inte vid UseRedis-anropet. Live ändring av prefix/anslutning stöds
inte; den kräver ny container/restart. Användarens IDistributedCache är aldrig reserv.

Redis/SQL-options bindas med IOptions och ValidateOnStart. SQL-anslutning kan komma från en DI-fabrik.
Inget startupkrav på frisk L2: valida inställningar kan starta med degraderad anslutning.
SQL-schemat har en separat versionsrad/tabell, migreringsskript och fail-fast vid okänd/incompatible version.
En schemaändring får inte vara en oannonserad automatisk migrering under rullande drift.

UseJson tar source-generated JsonSerializerContext eller validerade options. UseMessagePack kommer från
det versionsmatchade paketet. VerifyRoundTrip är true som standard och kan stängas av; hjälpkontrollen
bevisar inte semantisk likhet eller bevarande av nästlad derived/private state.
Generatorn varnar för riskabla deklarerade returformer, men användarens konfigurerade polymorfa serializer
kan göra dem giltiga. Distributed=false kringgår både L2-läsning och skrivning för den metoden.
HTTP använder egen kodare och passerar aldrig användarens serializer eller RoundTrip.

## 9. HTTP-caching: regler, generationer och synlighet

AddHttpCaching kräver AddMethodCache vid startup men dess inbördes registreringsordning är fri.
HTTP-lagringen är en intern adapter till Core:s motorimplementation och owned transport, inte ett eget
publikt lagringssystem och inte ett anrop till metodcachens generiska GetOrCreate-policy.

HTTP-handlern behåller Cache-Control, Age, Vary, Authorization/private/shared-cache-isolering,
ETag/Last-Modified/304, request-direktiv, stale-while-revalidate och stale-if-error.
Duration, metod-jitter, metod-fail-safe, metod-Enabled och MaxExpiration påverkar inte HTTP.
Explicit Cookie/Authorization headers deltar i identitet enligt befintliga regler.
Svaret ägs av anroparen; cachelagrade bodies/headers lämnas inte ut som muterbara delade objekt.

### 9.1 Separata budgetar

Utan L2 används samma MemoryCacheEngine-implementation med en separat TinyLFU-instans för HTTP.
MaxHttpBytes är en global budget för alla klienter, default 100 * 1024 * 1024, och räknar body-bytearray,
lagrade headernamn/värden och en dokumenterad konservativ metadata-overhead. Det är lagringskostnad, inte
ett löfte om mätning av hela CLR-heapen. Metodposters vikt påverkar inte HTTP och tvärtom.

En representation vars lagringskostnad är större än budgeten returneras men sparas inte.
ResponseBodyBuffer:s befintliga gräns/streaming-egenskaper bevaras; storlekskontroll får inte kräva
obegränsad buffring av ett svar som ändå inte ska lagras.
Engine:s bounded-write-buffer-marginal och concurrent-write-bound gäller och dokumenteras även i bytes.
Eviction får kasta värdet men inte återanvända en gammal generationsidentitet.

### 9.2 Stark invalidation i L2-läge

Med Redis/SQL lagras representationer bara i L2. Varje uppslag läser aktuell delad generation; ingen
lokal cache av generation, Vary-selector eller taggstatus får kringgå freshness/isolation-kontrollen.
L1-backplanen är inte beroendet för HTTP:s garanti.

Använd en formatoberoende generationsvektor för global HTTP-clear, klient, URL och konfigurerade
applikationstaggar. URL-delen använder bounded slots (nuvarande 4096 kan behållas); slotkollisioner orsakar
bara extra missar. Varje komponent är en unik aldrig återanvänd identitet och hålls som metadata utan expiry.
Saknad/evicted metadata får aldrig återgå till tom/startnamespace: skapa en ny identitet så att gamla
representationer blir oåtkomliga. Generationer läses igen från L2 för varje nytt uppslag.
Alla värdeformat använder samma generationsnycklar.

Owned transport erbjuder atomiska EnsureEpoch och AdvanceEpoch för HTTP-metadata. Ensure skapar endast
om posten saknas. Advance uppdaterar en monoton revision i samma operation och returnerar epoch-identiteten
som består av ett initierings-id och revision. En försenad eller upprepad Advance kan bara skapa en senare
revision, aldrig återställa en tidigare identitet. Saknad post/overflow skapar ett nytt initierings-id.
Redis gör detta i en Lua-operation och SQL i en transaktion med row/range-lock. Detaljerat byteformat
fastställs i PR 4 och fryses för wire v1. Ovillkorliga Set av tidigare observerade epochs är förbjudna,
även genom retries. Dessa primitiv kan också användas för HTTP-tags och Clear utan värde-enumeration.

HTTP etablerar atomiskt en HTTP-epoch-markör på gemensam tagg/clear-metadata innan den fångar sin första
generationsvektor. Den gemensamma transportens tagg-/clear-invalidation uppdaterar då både metodstamp och
HTTP-revision i samma backendoperation. Det fungerar även när avsändarnoden inte laddat HTTP-tillägget.
Metadata med HTTP-markör hålls utan expiry; vanliga metodtaggar behåller bounded retention. HTTP-markörer
skapas bara för global clear, klientnamn och konfigurerade statiska HTTP-tags, inte för godtyckliga
metodargument. En ny markör får inte återanvända en gammal identity om metadata saknas.

Transporten hämtar den delade HTTP-generationsvektorn i en bounded batch/pipeline, med högst en
metadata-läsrunda per normalt uppslag, följt av läsningen av representationen. Motsvarande metodläsning
hämtar värde och logisk key-tombstone ihop där provider stöder det. Korrekthetskontrollerna får inte
implementeras som en extra serie nätverksresor per komponent utan att §10:s grind uppfylls.

Svaret och Vary-selector lagras under den generationsvektor som fångades före origin-läsningen.
En sen origin/304/body-publicering hamnar i den gamla nyckelrymden och kan inte bli åtkomlig efter mutation.
En ny representation väljer aldrig aktuella generationer i efterhand för att återpublicera en gammal läsning.

Efter en 200–399 unsafe response måste POST/PUT/PATCH/DELETE bekräfta en ny URL-generation för request-URI
och relevanta samma-origin Location/Content-Location-mål enligt befintliga regler innan response returneras.
Med bekräftad generation får uppslag som börjar därefter på andra noder inte välja den tidigare generationen.
Redan pågående läsningar får avslutas med sitt tidigare svar.

Om generationsbytet inte kan bekräftas: kasta ett tydligt HTTP-cache-invalidationsfel, dispose upstream-
response enligt befintligt ägande, och behåll nödvändig återhämtning. Origin-mutationen kan ha lyckats;
biblioteket återförsöker **inte** POST/PUT/PATCH/DELETE. Caller cancellation efter origin-mutationen kan
avsluta väntan men får inte avbryta den nödvändiga generationsåterhämtningen.
Misslyckad generationsläsning ger origin/bypass, aldrig en gammal cacheträff.

Applikationens InvalidateTags för http:{client}, valfria klienttags och admin Clear använder samma HTTP-
generationskomponenter. Resultatet Distributed räknas först när generationsbytet är bekräftat; Queued
utlovar ingen omedelbar fjärreffekt. Public POST-invalidation är stark och returnerar därför inte normal
framgång med bara Queued. Per-entry admin removal pensionerar den logiska representationens URL-generation
och alla variants/format, utan att behöva lista dem. Clear pensionerar global HTTP-generation.
I minnesläge används motsvarande processlokala generationer, som inte försvinner före värdena.

HTTP-klienttaggen byggs med centralt escape av klientnamnet. Credentials och råa URL-queryvärden används
inte som admin/metrikidentitet. Klienter och callers förblir isolerade även när generation-slots kolliderar.

### 9.3 Cookies

För kända SocketsHttpHandler/HttpClientHandler stänger AddHttpCaching av UseCookies efter samtliga
handlerkonfigurationer och loggar ändringen en gång per klient utan cookie/URL-innehåll.
KeepCookies=true lämnar transporten helt orörd och kringgår response-läsning, sparande, revalidation och
stale-reserv. Only-if-cached ger då 504 utan origin. Det är ingen race-baserad CookieContainer-inspektion.

En lyckad unsafe mutation på en bypass-klient pensionerar fortfarande eventuell gemensam HTTP-generation
för samma klient/URL. Bypass är inte ett sätt att lämna andra callers gamla cachevärden kvar.
Set-Cookie vidarebefordras till rätt origin-anropare men sparas inte som delad header.
Dolda/okända transports under cachen bypassas som i dag; AllowUnverifiedTransport är en uttrycklig
trust-gräns för verifierade cookiefria transports, och får inte slå av kända cookie-skydd.

### 9.4 Administration och statistik

Utan L2 listar admin verkliga lokala HTTP-poster. Med L2 håller Core ett separat, bounded lokalt
observationsregister om högst 1024 HTTP-representationer per container. Det sparar hashed logisk id,
client, format, storlek, tags och deadline, aldrig body, credentials, cookieheader eller rå query.
Observationer ersätts/evictas separat och går ut vid representationens deadline; det är inte svarscaching.

Admin visar Storage=LocalMemory eller ObservedDistributed och tydlig text att den senare är en historisk
observation på den här noden, inte bekräftad närvaro eller global L2-inventering.
Miss/not-storable/successful known invalidation pensionerar relevanta observationer lokalt.
Efter restart är listan tom tills noden observerar L2 igen. En annan nods evictions/omarkerade deletion kan
göra en observation inaktuell; ingen dold backend-enumeration eller nätverksvalidering sker i ListEntries.

Stats och metrik räknar observationsantal separat från faktiska lokala entries/bytes. Admin removal av en
observations-id använder det lagrade logiska URL-målet för generationsinvalidation; den gissar ingen eN-key.
Ett okänt observations-id ger NotFound, inte en påhittad global lookup. Admin-tag/clear fungerar ändå utan
några observationer. Lokalt register ger ingen kvittens om alla noder och kan inte återskapa värden.

## 10. Prestandagrind

Varje beteendeändrande PR jämför sin merge-base och ändringen på samma maskin/runtime/GC-inställningar,
tre oberoende BenchmarkDotNet-körningar vardera med växlande bas/ändringsordning.
Redovisa full SHA, SDK/runtime, CPU, GC, lyssnare, datamängd, medel och 95-procentigt konfidensintervall
för körningsmedlen. L2 mäts separat med lika backend/latency, inte som ett nanosekundbenchmark.

Försämring stoppar PR om ändringens intervall ligger helt över basens och skillnaden är större än 3 %.
Ett brett/osäkert intervall är otillräcklig evidens, inte ett pass; upprepa/mät isolerat.
Allokeringar får inte öka på motsvarande träffvägar vid samma listener-inställning. 84 ns är historisk
baslinje, inte ett universellt absolut gränsvärde. PR 7 jämför även helheten med f43854d för att upptäcka
ackumulerade små försämringar. Nytt arbete mäts separat från befintliga jämförbara scenarier.

Obligatoriska scenarier: sync/Task/ValueTask hits och misses; simple/record/collection/fallback-key;
L2 read/write och RoundTrip av/på; contention/single-flight/eviction/weighted pressure; HTTP local/L2
inklusive generations-I/O; cookie-disabled caching och KeepCookies-bypass; telemetri utan/med lyssnare.
Befintliga benchmarkkontrakt för setup/nycklar/cleanup ska fortsatt vara gröna.

Prefixhash-PR:n mäter även nyckelbygge, hash/uppslag och hela genererade träffvägen separat, med korta och
långa prefix, växande buffertar, reservnycklar och både statistik/lyssnare av och på. IMemoryCache med
färdig nyckel, IMemoryCache med nyckel byggd per anrop och sync kontra Task-resultat redovisas som skilda
scenarier. Ett snabbare delbenchmark ersätter inte full-path-grinden. Uppskattningen 15–20 ns eller cirka
20 procent från förslaget Snabbare cacheträffar är en hypotes tills full-path-resultaten verifierats enligt
denna sektion; den förs inte in i README som ett uppmätt produktlöfte.

## 11. Konkreta acceptanstester

Test-id:n nedan är stabila spårnings-id:n; metoderna följer repositoryts vanliga Method_State_Expected-namn.
Varje test visar input, styrd interleaving och observerbart utfall. Tidsberoende använder FakeTimeProvider;
TaskCompletionSource/barriärer styr samtidighet, inga sleeps som bevis. Fel/tappade meddelanden använder
FaultInjection. Redis- och SQL-contract-tester delar scenariodefinitioner men körs mot riktiga providers.
Properties gäller för typer/Unicode/framing. Docs-löften har även en Docs.Tests-variant.

### PR 1: fasad, bindning och kompatibilitet

| ID | Arrangera/agera | Krävt utfall |
|---|---|---|
| G01 | Kör fryst vendor-DLL mot ny Core utan att regenerera; sync/Task/ValueTask, tags, invalidation och CacheInterface. | Rätt värden, samma nyckelidentitet, samma cachebeteende; manifest-hash oförändrad. |
| G02 | Module initializer registrerar kontrakt 99 med känd bootstrap; Describe/factory har en räknare som kastar om den körs. | Assembly kan laddas; AddMethodCache ger named versionfel; räknaren är noll, ingen TypeInitializationException. |
| G03 | Analysera semantiska symbols i generated sources för hela SignatureCorpus. | Alla runtimeanrop till MethodCache går genom Generated; användarsymboler/BCL tillåts. |
| G04 | Två containrar, samma moduler, olika overrides/managers; dispose den ena. | Ingen policy/generation/metrik/scope- eller disposalpåverkan i den andra. |
| G05 | Lägg cachebar DI-registrering efter AddMethodCache; jämför icke-cachebar sen registrering och upprepat AddMethodCache. | Bara den sena odekorerade cachebara registreringen ger tydligt fel; ingen dubblering. |
| G06 | AutoRegister scoped med 0, 1 och 2 kandidater; explicit registrering med 2 kandidater. | 0/2 ger named fel, 1 blir scoped; explicit registrering behåller sin implementation/lifetime. |
| G07 | Samma fixture under JIT och Native AOT; inkluderar modul i refererat hjälpbibliotek och explicit modulval. | Samma kandidater och dekoratorer, ingen assemblynamnsbaserad gissning. |
| G08 | Två keyed scoped registreringar av samma interface med olika resultat; generiska closed instanser och bakgrundsrefresh. | Isolerade nycklar/resultat; rätt inner/service key i ny scope; ingen användning efter dispose. |
| G09 | Stoppa anrop efter Capture av v1; byt till v2; fortsätt key-build/probe/load. | Ingen v2-policy skrivs under v1-nyckeln; stale snapshot bypass/retry enligt §2.2. |
| G10 | Packa/installera alla sex tillägg och Core; försök blandade versioner. | Exakt nuspec-intervall för varje tillägg; matchade versioner fungerar, mismatch nekas av strikt restore (NU1608 som error). |
| G11 | Reservnycklar, sealed/provider/record/samling och Unicode-surrogates med samma typade data. | Samma deterministiska identitet som helper; olika data/typer aliasar inte. |
| G12 | Capture A; starta inline-/reservbyggare; byt policy till B före uppslag och försök även byggaren med en annan MethodCall/container. | A använder bara A:s prefix och stale-regel; felbindning avvisas före cacheåtkomst. Ingen extra policy-läsning döljer racen. |
| G13 | Tom/liten/exakt full stackbuffer, långt prefix, långa escaped strängar, provider-parts och Unicode; styr heap-growth och miss följd av load. | Exakt samma text som helper vid alla storlekar; miss lämnar nyckeln användbar; bara skrivna tecken läses och async-arbete äger sin materialiserade nyckel. |
| G14 | Kompilera hela SignatureCorpus för sync/Task/ValueTask samt generiska/provider/reservformer under stödda TFM och AOT; kör hit-benchmarks. | StartKey/ref KeyBuilder och reservbyggaren kompilerar utan ref-escape; inga extra hit-allokeringar mot respektive TFM/nyckelforms baslinje. |

### PR 2: användar-API och policyprimitiver

Implementationsresultat och kvarvarande mergegrindar finns i [PR 2:s valideringsrapport](design-pr2-validation.md).

| ID | Arrangera/agera | Krävt utfall |
|---|---|---|
| P01 | Kör varje rad i §5.1: fyll, stoppa en load/refresh, ändra policy, släpp arbetet. | Nästa lokala uppslag använder ny generation; gammal laddning publicerar aldrig över den. |
| P02 | A/B delar L2; TTL=1h. A får privat override 1s; B återfyller efter method-invalidation. | A läser inte B:s 1h-post; efter FakeTime +2s måste A ladda på nytt, B behåller sin policy. |
| P03 | A/B har samma effektiva config men registrerar källor/tags i olika ordning. | Samma shared-identitet och verklig L2-reuse; audit-only ändring orsakar inte ny load. |
| P04 | A reload till 1s; B kvar på 1h skriver efter A:s invalidation. | A väljer inte B:s gamla policyrymd; när B reloadar delar de ny shared-identitet. |
| P05 | Override av/på, ändring, remove och restart med gammal privat L2-data kvar. | Ny privat identitet per ändring/restart; remove väljer shared efter bekräftad invalidation; inga gamla värden återkommer. |
| P06 | L2-post publicerad vid t0, duration=10s; en ny nod läser vid t0+9s. Kör också med en lång factory före t0 och med CacheNullValues=false hos mottagaren. | Lokal post går ut senast t0+10s; factorytid skiljs från CreatedAt; null-hit avvisas när null-caching är av. |
| P07 | Grupp-default Enabled=false i config, explicit metodtrue i attribut; även metodfalse med grupptrue. | Metodspecifik inställning vinner oavsett source-priority; falskt värde bypassar allt. |
| P08 | Enabled=false; många samtidiga anrop, stale-post och refresh kvar; CacheInvalidate körs. | Implementation körs per anrop utan joins/cache/stale; refresh lagrar inget; invalidation fungerar. |
| P09 | Egen async recording-manager och NoOp; även sync-genererad tjänst. | Async går till vald manager med rätt options; NoOp laddar varje gång; sync-custom bypassar utan task-blockering. |
| P10 | Built-in sync med native provider; sedan async-only/unready lånad anslutning och pågående async refresh. | Native bounded operation eller fail-open L1/factory, aldrig väntan på async task; korrekta generationer. |
| P11 | Invalidation för tre distinkta targets: två L2-success, ett storage-fel; en publication-failure. | Requested=3, Local=3, Distributed=2, Queued=1, Published=1, PublicationQueued=1; inga falska alla-kvittenser. |
| P12 | Tom/dubblett/invalid batch; cancellation före mutation och efter lokal mutation före L2. | Validering först; exakta räknare; tidig cancel gör inget; sen cancel avslutar väntan men återhämtningen slutförs. |
| P13 | Tappa method-frame till B eller håll L2 nere; låt A uppdatera. | A:s lokala policy gäller direkt; B kan behålla gammal L1 till expiry; queue/status och dokumentation matchar. |
| P14 | Supply mutable options/tags och ändra originalsamlingen efter publicering; CacheWhen avvisar ett L2-värde. | Publicerad snapshot oförändrad; avvisat L2-värde används/lagras inte, factory körs. |
| P15 | Bulk med L1/L2-hits, null/0/false, missar och samtidig writer. | Factory får bara missar; slutvärdet följer publication-vinnaren; max samtidiga L2-reads är bounded. |
| P16 | Reflektera offentliga async-metoder i användar-API och Generated-loader/invalidation. | Token sist; bootstrap och synkrona helpers undantas; inga default(T)-som-miss-kontrakt. |

PR 2 inför dessa identiteter, lokal generation, tombstone-läsning, nödvändiga envelope-tidsfält och
resultat/queue-primitiver i befintligt L2-flöde, med nuvarande provideradaptrar.
P02–P13 körs mot fault-store samt Redis och SQL innan merge.
De får inte skjutas upp till PR 4; där ändras physical-format/options och rullande kompatibilitet.

### PR 3: syntax, keys och reserverade identiteter

| ID | Arrangera/agera | Krävt utfall |
|---|---|---|
| K01 | Samma parsercorpus i attribut/generator/analyzer/config: 30s, 1.5h, 1d, 1.00:00:00, fraktion, whitespace/case. | Samma ticks över net8/9/10, sv-SE/tr-TR; alla giltiga former accepterade. |
| K02 | 30, 0s, -1h, 1m30s, 1,5h, exponent, overflow och positivt sub-tick-värde; analyzers av. | Generator/config ger named fel, aldrig tyst default; felaktig generatorinput producerar inget cachebeteende. |
| K03 | Global FailSafe på; metodtext off och builder-zero; unset och retry/timeout=zero. | Off/zero stänger av för metoden; unset ärver; retry/timeout-zero nekas. |
| K04 | Version utelämnad, explicit 0 och -1; Enabled/Distributed unset kontra false. | Frånvaro faller igenom; explicita giltiga värden respekteras; negativ Version nekas. |
| K05 | Guid/DateTime/offset/enum/null och separators i Cache/CacheInvalidate-mallar. | Samma Part-text och träffsäker invalidation i alla kulturer. |
| K06 | Användartaggar/keytext som imiterar interna method/ClearAll-namn, U+0000 och lone surrogates. | Bara rätt applikationstag påverkas; ingen intern generation/clear kan förfalskas. |
| K07 | Properties för overload/generic arity, closed types, keyed suffix och manual version-namespace. | Ingen key-aliasing; G11-korpus fortsatt grön. |
| K08 | Konsumera gamla raw-key-/attributformer och gamla diagnostik-id-konfigurationer. | Avsiktligt nytt API/diagnostik rapporteras; CHANGELOG har id-mappning och migration utan shims. |

### PR 4: transport, format och återhämtning

| ID | Arrangera/agera | Krävt utfall |
|---|---|---|
| D01 | Fyll key i N och N+1 samt två policyidentiteter; invalidiera från respektive version. | Alla äldre logiska varianter blir L2-missar även utan backplane, inte bara avsändarens format. |
| D02 | Samma fixture för tag, method och ClearAll; Unicode-identiteter och nytt frivilligt wire-fält; Ensure/Advance markerad HTTP-metadata. | Båda versioner förstår invalidationen; okända optional fields påverkar inte identity; atomiska epochs kan inte återställas av omvänd leverans/retry. |
| D03 | Oläsbar ram/okänd obligatorisk operation med fylld L1. | Lokal ClearAll, engångsvarning utan payload; ingen falsk leveransgaranti. |
| D04 | Gammalt format har TTL=1h; key-remove, fram till +6min; metadata outlives fortfarande posten. | Gammal post återkommer inte när tidigare femminutersstamp skulle ha gått ut. |
| D05 | Stoppa äldre write före fysisk transportcompletion; remove/newer write; släpp gammal transport. | Gammal post läses aldrig som giltig efter invalidation; recovery/bounds följer §8. |
| D06 | Försök korta retention efter att långlivad post lagrats; äldre/nyare atomic stamp-writes i omvänd ordning. | Retention/max-stamp går aldrig bakåt; inkompatibel scope-retention nekas utan resurrection. |
| D07 | Configure KeyPrefix efter UseRedis/UseSqlServer men före container/start. | Data, metadata, generationsnycklar och backplane använder samma prefix; annan app är isolerad. |
| D08 | Optionsvalidering utan frisk L2; DI-anslutningsfabrik; okänt SQL-schema och levererat migreringsskript. | Fel i inställning/schema ger tydliga fel; giltig outage startar degraderat; inget tyst schema-byte. |
| D09 | Private setter, Dog i List av Animal, korrekt polymorf context, RoundTrip på/av och Distributed=false. | Kontrollens begränsning dokumenterad/testad; context bevarar data; false gör inga L2-read/write. |
| D10 | Queue retry/publication failure, shutdown och reconnect med tappade meddelanden. | Kö observerbar/bounded, rätt lokala clears, korrekt lost-at-shutdown-rapport och inga oobserverade exceptions. |

### PR 5: telemetri och rapportering

| ID | Arrangera/agera | Krävt utfall |
|---|---|---|
| T01 | Två containrar med MetricCollector; fast hit, miss, L2-hit, flight-join, refresh, stale och exception. | Exakt en request-result per anrop; load-duration bara för faktisk origin/factory; containrar isolerade. |
| T02 | Befintliga keys/tags/token/query innehåller canary-identiteter; defaults och explicit span-opt-in. | Inga canaries i metrik/standardspans/loggar; opt-in gäller bara avsedda span-fält. |
| T03 | Registrera instrumentation före/efter Core och slå av lyssnare. | Samma instrument; inga dekoratordubletter; allocation- och tidsgrind klaras. |
| T04 | Storage queue och publication queue, L2 healthy men backplane disconnected/connecting/unknown. | Kötyper/status separata; failureStatus respekteras; healthy L2 döljer inte backplanefel. |
| T05 | Stats med method vikter, HTTP local bodies och bara observerade L2-svar. | Rätt enheter och scope; observerade poster ökar inte resident entries/HTTP-bytes. |

### Separat prefixhash-PR efter PR 5

| ID | Arrangera/agera | Krävt utfall |
|---|---|---|
| F01 | CsCheck över slumpade seed, prefix/argument och alla delningspunkter; tomma delar, UTF-16-surrogates, escaping, olika buffertstorlekar, Append efter lookup och reservnycklar. | Delad hash är exakt samma som helsträngshash för samma sluttext/seed; growth och Append lämnar aldrig gammal hash aktiv. |
| F02 | Tvinga hashkollisioner via en intern testhook; skriv/hämta/ersätt/ta bort genom string-, span- och builder-vägar, inklusive konkurrerande publicering och eviction. | Bara identisk ordinal nyckel är en träff; alla vägar hittar samma post; borttagning och frekvensspårning fungerar trots kollisioner. |
| F03 | Två processer med olika seed delar Redis/SQL; MethodCacheKeys.For, key/tag/clear-invalidation, admin och restart. Byt även policy under ett fångat anrop. | Nyckeltext och L2-identitet oförändrade; samma data delas och invalidieras; inget processhashvärde lagras/sänds och snapshot-isoleringen består. |
| F04 | Granska algoritm/seed/hotmodell; kör algoritmens referensvektorer och dokumenterade adversariala kollisionsfall med långa gemensamma prefix och kontrollerad arbetsmätning. | Dokumenterat skydd mot avsiktlig hashflooding; regressioner mäts utan sleeps eller sköra nanosekundasserts. Slumpmässig fördelning ensam räknas inte som godkännande. |
| F05 | Kör fryst PR 1-vendor-DLL mot prefixhash-Core utan regenerering; hela hit/miss-vägen på net8/9/10 och AOT, med korta/långa prefix och lyssnare av/på. | Samma kontrakt och cachebeteende; manifest oförändrat; full-path-resultat och allokeringar klarar §10, ingen funktion tas bort för vinsten. |

### PR 6: HTTP och admin

| ID | Arrangera/agera | Krävt utfall |
|---|---|---|
| H01 | Befintliga HTTP freshness/no-cache/private/shared/Vary/304/ownership-tester mot ny adapter. | Alla tidigare säkerhets- och protokollgarantier bevarade; metodduration/jitter/fail-safe ändrar inte svar. |
| H02 | A:s origin/body-publicering stoppad; B POST/PUT/PATCH/DELETE bekräftas; gammal A fortsätter. | Nytt uppslag på A/B väljer nya generationer; gammal response kan avslutas men återpubliceras inte. |
| H03 | Tappa alla backplane-meddelanden; A/B delar L2 och gör samma H02-fall. | Fortsatt korrekt via delade generationer; inga lokala värde/epoch-kopior används. |
| H04 | Origin lyckas; generation-write/get hänger/felar; cancel efter origin; slå sedan på L2. | Tydligt fel/cancel, origin körs en gång, återhämtning fortsätter; ingen normal framgång enbart Queued. |
| H05 | Evict generationmetadata, kör N/N+1, collisions i URL-slots; dispatcha Advance i omvänd ordning och upprepa en tidigare operation efter ny invalidation. | Atomisk revision går bara framåt; ingen återgång till gammalt namespace; collisions endast extra missar, inga caller/klientläckor. |
| H06 | Seed alla Vary/caller/formatvarianter; admin client-tag/custom-tag, Clear och per-observation remove under äldre publication. Skicka även tag/clear från en Core-nod utan HTTP-tillägg. | Alla berörda gamla varianter oåtkomliga utan enumeration; gemensam metadata pensioneras även av Core-only-noden; resultat markerar bekräftat/queued korrekt. |
| H07 | Unsafe svar med samma-origin Location/Content-Location samt främmande origin. | Rätt request- och mål-URL:er pensioneras; främmande origin invalidieras inte. |
| H08 | Cookies enabled genom konfiguration före/efter AddHttpCaching; KeepCookies false/true. | False stänger av/loggar en gång; true bevarar transport och ger full response-bypass, Only-if-cached=504. |
| H09 | Samtidiga cookies, Set-Cookie på 200/304, redirect och dold transport. | Ingen annans body/cookie återanvänds; inga jar-snapshot-races; okänd transport bypass om ej uttryckligen betrodd. |
| H10 | Bypass/KeepCookies-klient muterar en URL med redan delad response-generation. | Origin körs, generation pensioneras; bypass lämnar inte gamla variants åt andra callers. |
| H11 | Local HTTP och metodposter fyller sina budgetar; en HTTP-body större än hela MaxHttpBytes. | Separat eviction; stor body returneras/streamas men sparas inte; bounded kostnadsberäkning. |
| H12 | L2-mode observerar >1024 representations; restart, remote delete och stats/listing. | Register högst 1024, tomt efter restart; ObservedDistributed tydligt, aldrig global/resident garanti. |
| H13 | L2 saknas/egen app-IDistributedCache finns; AddHttpCaching utan Core och omvänd registreringsordning. | Ingen app-cache-reserv; saknad Core ger fel; legitim inbördes ordning fungerar. |
| H14 | Admin observation med credential/query-canaries och långt adversarialt klientnamn. | Bounded hashed identity; ingen body/credential/query läcker; tagg-escaping kan inte välja annan klient. |

### PR 7: slutgrind

| ID | Verifiering | Krävt utfall |
|---|---|---|
| V01 | Alla normala tester net8/9/10, Windows och kulturjobb; riktiga Redis/SQL required. | Inga nya oförklarade skips; relevanta regressions/fault/property/soak-jobb gröna. |
| V02 | Alla sju packade paket, package-smoke, demo, Native AOT och samtliga frysta vendor-DLL:er. | Installation, bounds, serializer-context och generator-contract fungerar utan regenererade baslinjer. |
| V03 | mdsnippets, generate-llms, PublicAPI och analyzer-releasefiler; användar-README/package-README matchar nya API. | Ingen drift eller döda exempel; endast utgiven kontraktsyta fryses. |
| V04 | §10 per förändring och hela baslinjen mot f43854d. | Reproducerbara resultat; ingen dold ackumulerad försämring eller extra hit-allokeringar. |

## 12. PR-ordning och klart-kriterier

| PR | Leverans | Testgrind |
|---|---|---|
| 0 | Detta versionshanterade dokument och länken från AGENTS.md. Ingen runtimeändring. | Markdown/länkar, contradictions/dependency-granskning, llms-regenerering utan drift. |
| 1 | Komplett Generated-fasad, snapshots/bindning, StartKey/ref KeyBuilder och buffertval, startup-builder/modulval och compat-baslinje. | G01–G14, relevanta signature/scope/AOT/package-tester och hit-benchmarks. |
| 2 | Smalt användar-API, options/policyprimitiver, identiteter, tombstones, overrides och InvalidationResult. | P01–P16 på en/två noder och båda providers. Befintliga adapters ändras nu där det behövs. |
| 3 | Keys/attributes/parser/diagnostik. | K01–K08, mdsnippets och AOT/signature corpus. |
| 4 | Owned options/providers, slutligt format/wire-v1, retention/schema och serialiseringskontroll. | D01–D10, P-regressionerna, packages och mixed-format-kompatibilitet. |
| 5 | Core-instrumentering, OTel-förenkling och admin/health-resultattyper. | T01–T05, privacy/allocation-tester och §10. |
| Efter 5, före 7 | Separat prefixhash-PR: oföränderligt prefixstate, intern motorjämförare, hitpath-benchmarks och verifierade docs-siffror. Kräver PR 1-kontraktet, inte ett nytt generator-kontrakt. | F01–F05, befintliga motor/security/property-tester och §10. |
| 6 | HTTP-adapter, generationsvektor, budgets, cookies och observationsadmin. | H01–H14, befintliga HTTP/transport/ownership-regressioner och §10. |
| 7 | Samlad releaseverifiering och slutlig dokumentationsgenomgång. | V01–V04 före Unshipped → Shipped. |

Varje PR har uppdaterad PublicAPI, XML docs, användar-README/feature/package-README, CHANGELOG,
mdsnippets och llms-filer när den ändrar respektive kontrakt. Säkerhetsändringar uppdaterar
[security.md](../security.md) och motsvarande tester i samma PR. Exempel behöver inte väntas till PR 7.
Varje PR mergas först på grön MethodCache CI enligt den arbetsordning Johan godkänner; detta dokument
ger inte fristående tillåtelse att publicera paket eller skicka meddelanden till andra chattar.

PR 1-baslinjen fryses först när stöd för keyed/scope/fallback/sync, den bundna byggaren och snapshot-racen
har verifierats. PR 1 äger alla ändringar i Generated-kontraktet; prefixhash-PR:n äger den interna hashningen.
Planen Snabbare cacheträffar som Johan delat införlivas genom §2.2, §2.4, §10, G12–G14 och F01–F05.
Dess CachedMethod.StartKey/TryGet-skiss ersätts av snapshotbundna MethodCall-ingångar enligt §2.2.
Ingen del väntar på att prefixhash-optimeringen ska vara färdig före PR 1. Kontraktet fryses däremot inte
med oberoende prefix/policy-läsningar eller en byggare som kräver framtida ändrade signaturer.
PR 2 får inte förlita sig på ej implementerade PR 4-primitiver. Transportförändringarna förläggs till PR 4,
men de små identitets-/tombstoneändringarna som behövs för P-testerna görs i PR 2.
S7 är avgränsat till senare release och ska inte smygas in som en prestandaförutsättning här.

Repositoryts vanliga [teststrategi](INTEGRATION_TEST_STRATEGY.md),
[runtime-konfigurationsöversikt](RUNTIME_CONFIGURATION_OVERVIEW.md) och [releasechecklista](RELEASE.md)
gäller fortsatt; deras nuvarande beteendebeskrivningar uppdateras i implementationen.
