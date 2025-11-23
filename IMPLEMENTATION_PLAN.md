# План добавления диагностического логирования

## Цель
Добавить метрики и логирование для выявления причин роста потребления памяти. Мы будем отслеживать размер кэшей метаданных и общее потребление памяти процессом.

## Предлагаемые изменения

## Предлагаемые изменения

## Предлагаемые изменения

### 1. OneSTools.EventLog (Библиотека чтения)

#### [MODIFY] [LgfReader.cs](file:///c:/Users/deevi/AntigravityProjects/EventLog/OneSTools.EventLog/OneSTools.EventLog/LgfReader.cs)
*   Добавить свойство `public int CacheCount { get; }` — количество объектов.
*   Добавить свойство `public long CacheApproxSize { get; }` — примерный размер данных в байтах (сумма длин строк * 2).

#### [MODIFY] [EventLogReader.cs](file:///c:/Users/deevi/AntigravityProjects/EventLog/OneSTools.EventLog/OneSTools.EventLog/EventLogReader.cs)
*   Проксировать свойства `LgfCacheCount` и `LgfCacheApproxSize`.

### 2. OneSTools.EventLog.Exporter.Core (Ядро экспорта)

#### [MODIFY] [EventLogExporterSettings.cs](file:///c:/Users/deevi/AntigravityProjects/EventLog/OneSTools.EventLog/OneSTools.EventLog.Exporter.Core/EventLogExporterSettings.cs)
*   Добавить свойство `public string InfoBaseName { get; set; }`.

#### [MODIFY] [EventLogExporter.cs](file:///c:/Users/deevi/AntigravityProjects/EventLog/OneSTools.EventLog/OneSTools.EventLog.Exporter.Core/EventLogExporter.cs)
*   Принимать `InfoBaseName` из настроек.
*   В `StartAsync` добавить периодическое логирование (каждые 1000-5000 событий).
*   Логировать метрики **с указанием имени ИБ**:
    *   **InfoBase**: Имя базы.
    *   **LGF Cache**: Количество элементов и примерный размер в МБ (специфично для этой базы).
    *   **Queue Depth**: `_batchBlock.OutputCount` и `_writeBlock.InputCount` (специфично для этой базы).
    *   **Managed Memory**: `GC.GetTotalMemory(false)` (общее для процесса, но корреляция важна).
    *   **GC Collections**: `GC.CollectionCount` (общее).

### 3. OneSTools.EventLog.Exporter.Manager (Менеджер)

#### [MODIFY] [ExportersManager.cs](file:///c:/Users/deevi/AntigravityProjects/EventLog/OneSTools.EventLog/OneSTools.EventLog.Exporter.Manager/ExportersManager.cs)
*   При создании `EventLogExporterSettings` заполнять свойство `InfoBaseName` (переменная `name`).
*   Добавить периодическую задачу (раз в 1-5 минут).
*   Логировать суммарное потребление памяти процессом и список активных баз.

## Ожидаемый результат
Логи будут позволять изолировать проблему до конкретной базы:
`[Exporter] [Base: UPP_Main] Items: 50k. LGF: 15k items (20MB). Queues: 50/0. Global Mem: 450MB.`
`[Exporter] [Base: ZUP_Archive] Items: 1k. LGF: 200k items (500MB). Queues: 0/0. Global Mem: 950MB.`

Здесь сразу видно, что `ZUP_Archive` потребляет 500МБ кэша, хотя событий мало. Это и есть "раздельный учет".
