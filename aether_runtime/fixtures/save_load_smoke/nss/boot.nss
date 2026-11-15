chapter main
{
    #SYSTEM_text_speed = 0;
    $SYSTEM_text_interval = 0;
    $SYSTEM_text_waitkey = true;
    if (#SAVE_SMOKE_COMMON == 0) {
        #SAVE_SMOKE_COMMON = 1;
        Save(0);
    }
    $metadata = #SYSTEM_save_path + "/roundtrip.npf";
    WriteFile($metadata, "保存 metadata");
    WriteFile($metadata, "|roundtrip", true);
    $roundtrip = ReadFile($metadata);
    CreateColor("background", 0, 0, 0, 800, 600, RED);
    CreateColor("snapshot_marker", 1, 640, 40, 100, 80, YELLOW);
    Wait(50);
    LoadImage("screen_image", SCREEN);
    CreateTexture("owned_screen", 1, 0, 0, "screen_image");
    Delete("screen_image");
    Delete("background");
    Delete("snapshot_marker");
    if ($roundtrip != "保存 metadata|roundtrip") {
        CreateColor("metadata_error", 50, 0, 0, 800, 600, #FF00FF);
        WaitKey();
        Exit();
    }
    CreateText("header", 20, 40, 140, 720, 60, "Persistent red checkpoint");
    CreateColor("window", 10, 40, 220, 720, 180, #202020);
    CreateWindow("dialogue", 20, 40, 220, 720, 180, false);
    LoadText("main", "dialogue", "checkpoint", 700, 160, 0, 32);
    Request("@checkpoint", Enter);
    Request("@checkpoint", Disused);
    WaitText("@checkpoint", 0);
    Delete("owned_screen");
    Delete("header");
    Delete("dialogue");
    Delete("window");
    if (ReadFile(#SYSTEM_save_path + "/phase.npf") == "phase1") {
        #SAVE_SMOKE_COMMON = 2;
        Save(0);
    }
    CreateColor("blue_branch", 0, 0, 0, 800, 600, BLUE);
    if (#SAVE_SMOKE_COMMON == 2) {
        CreateColor("common_marker", 5, 640, 40, 100, 80, GREEN);
    } else {
        CreateColor("common_marker", 5, 640, 40, 100, 80, #FF00FF);
    }
    CreateText("blue_text", 20, 40, 140, 720, 60, "Resumed blue branch; current common flags preserved.");
    WaitKey();
    Exit();
<PRE dialogue>
[checkpoint]
This dialogue and the owned SCREEN image must survive a cold restart.
</PRE>
}
