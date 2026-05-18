scp-radio-cycle-channel = Switch radio channel
scp-radio-toggle-radio = On/Off
scp-radio-current-channel = Current channel is now: { $name }
scp-radio-microphone =
    Microphone { $value ->
        [true] enabled
       *[false] disabled
    }
scp-radio-radio-status =
    Radio: { $value ->
        [true] [bold]On[/bold]
       *[false] [bold]Off[/bold]
    }
scp-radio-microphone-status =
    Microphone: { $value ->
        [true] [bold]On[/bold]
       *[false] [bold]Off[/bold]
    }
scp-radio-not-enough-charge = Insufficient charge
scp-radio-toggle-message =
    { $name } { $value ->
        [true] turned on
       *[false] turned off
    }
