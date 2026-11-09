' A VB.NET script: no project, no Imports needed for System, Linq, FH2 or FH2ModPlatform.
Public Class HiScript
    Inherits [Mod]

    Public Overrides ReadOnly Property Info As ModInfo
        Get
            Return New ModInfo("HiScript", "1.0.0") With {.Description = "A VB.NET script"}
        End Get
    End Property

    Protected Overrides Sub OnInit()
        Log($"hi from a VB script, reload: {IsReload}")
        AddCommand("hi", "says hi", Function(args) $"hi {args} from VB")
    End Sub
End Class
