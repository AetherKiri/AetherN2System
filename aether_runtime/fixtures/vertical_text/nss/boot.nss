chapter main
{
    #SYSTEM_text_speed = 0;
    $SYSTEM_text_interval = 0;
    $SYSTEM_text_waitkey = true;
    CreateColor("background", 0, 0, 0, 800, 600, BLACK);
    SetFont("fixture", 24, #D0B040, BLACK, Normal, null);
    SetTextOrientation(false);
    CreateWindow("box", 10, 60, 46, 690, 498, false);
    LoadText("main", "box", "page01", 690, 488, 0, 26);
    Request("box/page01", Enter);
    CreateColor("phase", 100, 0, 0, 8, 8, #FF0000);
    WaitKey();
    Delete("box");

    // Same inner rectangle as the generated legacy BOX2: x60..750, y56..544.
    // Swapping layout dimensions and rotating the page maps lines to columns.
    SetTextOrientation(true);
    CreateWindow("box", 10, 760, 56, 488, 700, false);
    LoadText("main", "box", "page02", 488, 690, 0, 26);
    Request("box/page02", Enter);
    SetVertex("box/page02", 0, 0);
    Rotate("box/page02", 0, 0, 0, 90, null, false);
    CreateColor("phase", 100, 0, 0, 8, 8, #00FF00);
    WaitKey();
    Delete("box");

    // The old @ font contract provides an independent orientation control.
    SetFont("@fixture", 24, #D0B040, BLACK, Normal, null);
    CreateWindow("box", 10, 760, 56, 488, 700, false);
    LoadText("main", "box", "page03", 488, 690, 0, 26);
    Request("box/page03", Enter);
    SetVertex("box/page03", 0, 0);
    Rotate("box/page03", 0, 0, 0, 90, null, false);
    CreateColor("phase", 100, 0, 0, 8, 8, #0000FF);
    WaitKey();
    Delete("box");

    // Change only orientation: font size, color and glyph shape must survive.
    SetTextOrientation(false);
    CreateWindow("box", 10, 60, 46, 690, 498, false);
    LoadText("main", "box", "page04", 690, 488, 0, 26);
    Request("box/page04", Enter);
    CreateColor("phase", 100, 0, 0, 8, 8, #FFFFFF);
    WaitKey();
    Exit();

<PRE box>
[page01]
<FONT incolor=RED>上</FONT><FONT incolor=GREEN>下</FONT><FONT incolor=BLUE>左</FONT>永
</PRE>
<PRE box>
[page02]
<FONT incolor=RED>上</FONT><FONT incolor=GREEN>下</FONT>永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永<FONT incolor=BLUE>左</FONT>
</PRE>
<PRE box>
[page03]
<FONT incolor=RED>上</FONT><FONT incolor=GREEN>下</FONT>永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永永<FONT incolor=BLUE>左</FONT>
</PRE>
<PRE box>
[page04]
<FONT incolor=RED>上</FONT><FONT incolor=GREEN>下</FONT><FONT incolor=BLUE>左</FONT>永
</PRE>
}
