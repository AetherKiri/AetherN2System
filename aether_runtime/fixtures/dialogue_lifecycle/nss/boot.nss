chapter main
{
    #SYSTEM_text_speed = 0;
    $SYSTEM_text_interval = 0;
    $SYSTEM_text_waitkey = true;
    CreateColor("background", 0, 0, 0, 640, 360, BLACK);
    CreateWindow("dialogue", 10, 20, 20, 600, 280, false);
    CreateColor("window", 1, 20, 20, 600, 280, #202020);
    Fade("window", 0, 0, null, false);
    LoadText("main", "dialogue", "first", 600, 280, 0, 32);
    WaitText("@first", 0);
    CreateColor("phase", 30, 0, 340, 20, 20, RED);
    WaitKey();
    Fade("window", 200, 1000, null, true);
    CreateColor("phase", 30, 0, 340, 20, 20, GREEN);
    WaitKey();
    Request("@first", Enter);
    Request("@first", Disused);
    WaitText("@first", 0);
    CreateColor("phase", 30, 0, 340, 20, 20, BLUE);
    WaitKey();
    LoadText("main", "dialogue", "second", 600, 280, 0, 32);
    WaitText("@second", 0);
    Request("@second", Enter);
    WaitText("@second", 0);
    Request("@second", Disused);
    CreateColor("phase", 30, 0, 340, 20, 20, WHITE);
    WaitKey();
    Exit();
<PRE dialogue>
[first]
The first page must stay hidden until its window is ready.
</PRE>
<PRE dialogue>
[second]
The second page must not retain the first page.
</PRE>
}
