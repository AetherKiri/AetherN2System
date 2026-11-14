chapter main
{
    CreateColor("background", 0, 0, 0, 640, 360, "BLACK");
    CreateText("title", 10, 20, 10, 580, 90, "<FONT incolor=GREEN>Aether N2 text</FONT> <RUBY text=むらまさ>村正</RUBY><BR>Inline font, ruby and line break");
    if (ImageHorizon("title") > 20 && ImageHorizon("title") <= 580 && ImageVertical("title") > 20 && ImageVertical("title") <= 90) {
        CreateColor("dimensions-ok", 20, 620, 0, 20, 20, "GREEN");
    }
    CreateWindow("dialogue", 10, 20, 140, 580, 190, true);
    LoadText("main", "dialogue", "page", 580, 190, 0, 32);
    WaitText("@page", 0);
    CreateColor("complete", 20, 620, 340, 20, 20, "GREEN");
    WaitKey();
    Exit();
<PRE dialogue>
[page]
First dialogue line <k>after the inline halt.

<FONT incolor=RED>Second dialogue page</FONT><k>
{CreateColor("inline-code", 30, 0, 340, 20, 20, "BLUE");}
Third line after inline code.
</PRE>
}
