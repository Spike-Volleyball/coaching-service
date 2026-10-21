using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Coaching.Infrastructure.Migrations
{
    /// <summary>
    /// Erases what account deletions left in this service's copies of their profiles. The deletion
    /// handler renamed the copy and blanked its email, counting on profiles' anonymised update to
    /// clear the rest; the two raced, and on prod every one of the 21 deleted accounts kept its
    /// date of birth here.
    ///
    /// A deleted account's copy is the only row the handler leaves under that name with an empty
    /// surname and inactive, so that is the match. The blanked email becomes NULL, as the
    /// handler now leaves it.
    /// </summary>
    public partial class EraseDeletedAccountProfiles : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "UserProfile"
                SET "Email" = NULL, "PhoneNumber" = NULL, "DateOfBirth" = NULL,
                    "ImageUrl" = NULL, "ImageThumbHash" = NULL, "IsEmailVerified" = FALSE
                WHERE "Name" = 'Deleted User' AND "Surname" = '' AND NOT "IsActive";
                """);
        }

        // Nothing to put back: the values are gone, as they should be.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
