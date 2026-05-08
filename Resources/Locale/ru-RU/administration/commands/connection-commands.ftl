## Strings for the "grant_connect_bypass" command.

cmd-grant_connect_bypass-desc = Временно разрешает пользователю обходить стандартные проверки подключения.
cmd-grant_connect_bypass-help = Использование: grant_connect_bypass <user> [duration minutes]
    Временно предоставляет пользователю возможность обходить стандартные ограничения подключения.
    Обход применяется только к этому игровому серверу и истекает по умолчанию через 1 час.
    Пользователь сможет подключиться независимо от белого списка, режима панического бункера или лимита игроков.
cmd-grant_connect_bypass-arg-user = <user>
cmd-grant_connect_bypass-arg-duration = [duration minutes]
cmd-grant_connect_bypass-invalid-args = Ожидалось 1 или 2 аргумента
cmd-grant_connect_bypass-unknown-user = Не удалось найти пользователя '{$user}'
cmd-grant_connect_bypass-invalid-duration = Неверная длительность '{$duration}'
cmd-grant_connect_bypass-success = Обход успешно добавлен для пользователя '{$user}'
