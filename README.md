<div align="center">

# 🧹 NakClean

### Честный чистильщик и оптимизатор Windows

Премиальный, прозрачный и **без плацебо** - только то, что реально работает.

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/UI-WPF-2C3E50)
![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?logo=windows&logoColor=white)
![Platform](https://img.shields.io/badge/arch-x64-blue)
![Version](https://img.shields.io/badge/version-1.5.5-DDB44B)
![License](https://img.shields.io/badge/license-proprietary-lightgrey)

</div>

---

## О проекте

**NakClean** - это аналог CCleaner / BoosterX / WizTree, написанный с нуля на **C# / .NET 8 + WPF**.
Главный принцип - **честность**: никаких «ускорителей ОЗУ» и фейковых твиков. Каждое действие либо реально освобождает место, либо реально меняет настройку Windows - и программа честно говорит, какой будет эффект.

Премиальный тёмно-золотой дизайн (есть и тёплая светлая тема), полная локализация **RU / EN**.

## ✨ Возможности

| Раздел | Что делает |
|---|---|
| **Обзор системы** | Живой монитор: CPU, ОЗУ, видеокарты (загрузка/температура/VRAM), диски, батарея + сводка по системе |
| **Проверка ПК** | Балл здоровья 0-100, проверки памяти/дисков/SMART/Защитника/драйверов/батареи с понятными вердиктами |
| **Очистка** | Мусор системы и браузеров, кэши программ (Discord/Spotify/Teams/Steam…), следы, корзина - с честным подсчётом места |
| **Оптимизация** | Реально работающие твики с метками эффекта, профили (Игровой/Тихий/Сбалансированный), журнал изменений + откат всего |
| **Обслуживание** | SFC, DISM RestoreHealth, очистка WinSxS, дефраг/оптимизация дисков, тест ОЗУ |
| **Реестр** | Поиск и исправление битых записей (с резервной копией перед изменением) |
| **Запуск** | Автозагрузка, запланированные задачи, службы, контекстное меню |
| **Удаление программ** | Менеджер установленных приложений |
| **Поиск файлов** | Быстрый обзор диска через чтение NTFS MFT + карта диска (treemap) |
| **Диагностика** | Здоровье дисков (SMART), батареи (износ/циклы/заряд/паспорт), анализ времени загрузки, паспорт ПК (HTML) |

## 🛡 Принципы

- **Без плацебо** - отброшены все «фейковые» оптимизации; остались только реально работающие.
- **Прозрачность** - перед удалением/изменением видно, что и зачем; реестр и твики откатываются.
- **Минимум зависимостей** - WMI и Win32 P/Invoke, плюс пакеты `System.Management` и `System.Diagnostics.EventLog`.
- **Локализация 100%** - русский и английский, переключение «на лету».

## 🧰 Стек

- **C# / .NET 8**, **WPF** (собственный лёгкий MVVM)
- WMI (`System.Management`), журнал событий (`System.Diagnostics.EventLog`)
- Win32 P/Invoke, COM через `dynamic`
- Без внешних UI-библиотек

## 🔧 Сборка

> Требуется [.NET 8 SDK](https://dotnet.microsoft.com/download).

```powershell
# Отладочная сборка
dotnet build NakClean.csproj -c Debug

# Portable (один .exe, без установки .NET у пользователя)
dotnet publish NakClean.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o bin/portable-publish
```

## 💻 Системные требования

- Windows 10 (1809+) или Windows 11, **x64**
- Права администратора (нужны для очистки / твиков / обслуживания)

## 📄 Лицензия

Проприетарный проект. © 2026 **nak** ([github.com/nakv1](https://github.com/nakv1)). Все права защищены.
Подробнее - в [LICENSE.txt](LICENSE.txt).

---

<div align="center">
Сделано <b>by nak</b> · <a href="https://github.com/nakv1">github.com/nakv1</a>
</div>
