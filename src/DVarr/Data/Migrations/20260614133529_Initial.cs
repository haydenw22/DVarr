using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DVarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelHealth",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Alive = table.Column<bool>(type: "INTEGER", nullable: false),
                    BitrateKbps = table.Column<int>(type: "INTEGER", nullable: true),
                    BlackRatio = table.Column<double>(type: "REAL", nullable: false),
                    FreezeRatio = table.Column<double>(type: "REAL", nullable: false),
                    SilenceRatio = table.Column<double>(type: "REAL", nullable: false),
                    Verdict = table.Column<string>(type: "TEXT", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    FingerprintHashesJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastProbedUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelHealth", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    RunAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    IntervalS = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Leagues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Sport = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Leagues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordingId = table.Column<int>(type: "INTEGER", nullable: true),
                    TsUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    FromState = table.Column<string>(type: "TEXT", nullable: true),
                    ToState = table.Column<string>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    DeliveredHa = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Programmes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    StopUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    EpgUid = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Programmes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Recordings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventId = table.Column<int>(type: "INTEGER", nullable: true),
                    SessionId = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    StreamId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EndUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PrePadS = table.Column<int>(type: "INTEGER", nullable: false),
                    PostPadS = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<string>(type: "TEXT", nullable: false),
                    DualCapture = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConflictPolicy = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FfmpegPid = table.Column<int>(type: "INTEGER", nullable: true),
                    SegmentDir = table.Column<string>(type: "TEXT", nullable: true),
                    OutputPath = table.Column<string>(type: "TEXT", nullable: true),
                    BytesWritten = table.Column<long>(type: "INTEGER", nullable: false),
                    LastFrameUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastContentOkUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    GapsJson = table.Column<string>(type: "TEXT", nullable: true),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                    ResolutionSnapshotJson = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recordings", x => x.Id);
                    table.UniqueConstraint("AK_Recordings_Id_SourceId", x => new { x.Id, x.SourceId });
                });

            migrationBuilder.CreateTable(
                name: "ScheduleTicks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TickUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RecordingsExamined = table.Column<int>(type: "INTEGER", nullable: false),
                    Started = table.Column<int>(type: "INTEGER", nullable: false),
                    Resumed = table.Column<int>(type: "INTEGER", nullable: false),
                    Finalized = table.Column<int>(type: "INTEGER", nullable: false),
                    Missed = table.Column<int>(type: "INTEGER", nullable: false),
                    Conflicts = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleTicks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Secrets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Secrets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "SourceLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InternalEventId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderEventId = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    HttpsPort = table.Column<int>(type: "INTEGER", nullable: true),
                    ServerProtocol = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Password = table.Column<string>(type: "TEXT", nullable: false),
                    MaxStreams = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderMaxConns = table.Column<int>(type: "INTEGER", nullable: true),
                    ProviderActiveCons = table.Column<int>(type: "INTEGER", nullable: true),
                    AllowedOutputFormats = table.Column<string>(type: "TEXT", nullable: true),
                    ExpDateUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    IsTrial = table.Column<bool>(type: "INTEGER", nullable: false),
                    Healthy = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastAuthAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LeagueId = table.Column<int>(type: "INTEGER", nullable: false),
                    NaturalKey = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StartUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EndUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    StartIsDateOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    Monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                    MonitoredLocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChannelLocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenSyncUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    SourceMetaJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Events_Leagues_LeagueId",
                        column: x => x.LeagueId,
                        principalTable: "Leagues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LeagueChannelMaps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LeagueId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    Pinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: true),
                    ActiveFromUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ActiveToUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeagueChannelMaps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeagueChannelMaps_Leagues_LeagueId",
                        column: x => x.LeagueId,
                        principalTable: "Leagues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecordingFallbacks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordingId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordingFallbacks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecordingFallbacks_Recordings_RecordingId_SourceId",
                        columns: x => new { x.RecordingId, x.SourceId },
                        principalTable: "Recordings",
                        principalColumns: new[] { "Id", "SourceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecordingSegments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordingId = table.Column<int>(type: "INTEGER", nullable: false),
                    Capture = table.Column<string>(type: "TEXT", nullable: false),
                    Seq = table.Column<int>(type: "INTEGER", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    StartUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Closed = table.Column<bool>(type: "INTEGER", nullable: false),
                    ContentVerdict = table.Column<string>(type: "TEXT", nullable: false),
                    Suspect = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordingSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecordingSegments_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    NameNorm = table.Column<string>(type: "TEXT", nullable: false),
                    LogicalKey = table.Column<string>(type: "TEXT", nullable: true),
                    EpgChannelId = table.Column<string>(type: "TEXT", nullable: true),
                    StreamId = table.Column<int>(type: "INTEGER", nullable: false),
                    TvArchive = table.Column<bool>(type: "INTEGER", nullable: false),
                    TvArchiveDuration = table.Column<int>(type: "INTEGER", nullable: true),
                    DetectedQuality = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Channels_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TunerLeases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false),
                    RecordingId = table.Column<int>(type: "INTEGER", nullable: true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: true),
                    StreamId = table.Column<int>(type: "INTEGER", nullable: true),
                    Pid = table.Column<int>(type: "INTEGER", nullable: true),
                    AcquiredAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastHeartbeatUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DeadlineUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    BytesWritten = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TunerLeases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TunerLeases_Sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "Sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventId = table.Column<int>(type: "INTEGER", nullable: false),
                    NaturalKey = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    StartUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EndUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    StartIsDateOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    Monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                    MonitoredLocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChannelLocked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventSessions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelHealth_ChannelId",
                table: "ChannelHealth",
                column: "ChannelId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Channels_LogicalKey",
                table: "Channels",
                column: "LogicalKey");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_SourceId",
                table: "Channels",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_SourceId_StreamId",
                table: "Channels",
                columns: new[] { "SourceId", "StreamId" });

            migrationBuilder.CreateIndex(
                name: "IX_EventSessions_EventId",
                table: "EventSessions",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_EventSessions_NaturalKey",
                table: "EventSessions",
                column: "NaturalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_LeagueId",
                table: "Events",
                column: "LeagueId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_NaturalKey",
                table: "Events",
                column: "NaturalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_StartUtc",
                table: "Events",
                column: "StartUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_State_RunAtUtc",
                table: "Jobs",
                columns: new[] { "State", "RunAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LeagueChannelMaps_LeagueId_Rank",
                table: "LeagueChannelMaps",
                columns: new[] { "LeagueId", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_TsUtc",
                table: "Notifications",
                column: "TsUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Programmes_ChannelId_StartUtc",
                table: "Programmes",
                columns: new[] { "ChannelId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Programmes_EpgUid",
                table: "Programmes",
                column: "EpgUid");

            migrationBuilder.CreateIndex(
                name: "IX_RecordingFallbacks_RecordingId_Rank",
                table: "RecordingFallbacks",
                columns: new[] { "RecordingId", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_RecordingFallbacks_RecordingId_SourceId",
                table: "RecordingFallbacks",
                columns: new[] { "RecordingId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecordingSegments_RecordingId_Capture_Seq",
                table: "RecordingSegments",
                columns: new[] { "RecordingId", "Capture", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_EventId_State",
                table: "Recordings",
                columns: new[] { "EventId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_StartUtc",
                table: "Recordings",
                column: "StartUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_State",
                table: "Recordings",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduleTicks_TickUtc",
                table: "ScheduleTicks",
                column: "TickUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Secrets_Name",
                table: "Secrets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceLinks_Provider_ProviderEventId",
                table: "SourceLinks",
                columns: new[] { "Provider", "ProviderEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_Sources_Label",
                table: "Sources",
                column: "Label",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TunerLeases_SourceId_State",
                table: "TunerLeases",
                columns: new[] { "SourceId", "State" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelHealth");

            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropTable(
                name: "EventSessions");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "LeagueChannelMaps");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "Programmes");

            migrationBuilder.DropTable(
                name: "RecordingFallbacks");

            migrationBuilder.DropTable(
                name: "RecordingSegments");

            migrationBuilder.DropTable(
                name: "ScheduleTicks");

            migrationBuilder.DropTable(
                name: "Secrets");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "SourceLinks");

            migrationBuilder.DropTable(
                name: "TunerLeases");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "Recordings");

            migrationBuilder.DropTable(
                name: "Sources");

            migrationBuilder.DropTable(
                name: "Leagues");
        }
    }
}
