ent-BaseIntercom = intercom
    .desc = An intercom. For when the station just needs to know something.

ent-BaseIntercomSecure = { ent-Intercom }
    .desc = { ent-Intercom.desc }

ent-Intercom = { ent-IntercomConstructed }
    .desc = { ent-IntercomConstructed.desc }

ent-IntercomConstructed = { ent-BaseIntercom }
    .suffix = Empty, Panel Open
    .desc = { ent-BaseIntercom.desc }

ent-IntercomSecurity = { ent-BaseIntercomSecure }
    .desc = An intercom. It's been reinforced with metal from security helmets, making it a bitch-and-a-half to open.
    .suffix = Security
