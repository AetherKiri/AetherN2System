chapter main
{
    $SYSTEM_text_interval = 48;
    #SYSTEM_text_speed = 200;
    #SYSTEM_auto_wait_per_character = 10;
    $SYSTEM_text_waitkey = true;
    CreateColor("background", 0, 0, 0, 640, 360, "BLACK");
    CreateWindow("dialogue", 10, 20, 20, 600, 280, true);
    LoadText("main", "dialogue", "page", 600, 280, 0, 32);
    Request("@page", Enter);
    WaitText("@page", 0);
    CreateColor("complete", 20, 620, 340, 20, 20, "GREEN");
    WaitKey();
    Exit();
<PRE dialogue>
[page]
First sentence fades character by character.<k>Second sentence stays on this page.
{CreateColor("inline-code", 30, 0, 340, 20, 20, "BLUE");}
After inline code stays in order.

New page must wait for another click.
</PRE>
}
