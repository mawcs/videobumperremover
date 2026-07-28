# Clarification on Terms and Usage for CLI and UI

This is a UX perspective on terms and interaction terms as used in the VBR product. The result being a

## 1. Library

A "library" is the user's media library. A user may have multiple libraries. Heck, a user may be a custodian of other user's libraries (i.e. My son's video library is on my NAS and I am the admin). A user may have multiple libraries in their mental model. Here are examples of distinct libraries (that the user subconsciously keeps unmingled, despite the fact that they might refer to all of this as a single "library" in their daily vocabulary):

- All of my studio-created videos. DVDs, TV shows, blurays, etc.
- All of my home movies
- All of the videos I've downloaded from YT, Reddit, etc.
- All of the training courses I've purchased
- All of the videos I've produced (with sponsor sections in them and the sponsor relationship is ended)

So the relationship of user to "libraries" is one-to-many. This is a conceptual, user term about their media.

Further, any given "library" could have, or is likely to have, multiple folders; multiple parent folders as well as multiple sub-folders. So, the relationship between a library and folders is one-to-many.

Thus, the relationship of user to folders is one-to-many-to-many. This should map to a user's mental model.

### "Named Library"

This is a conceptual term for the VBR team only. Once we ask a user to provide a "library name" and correlate that to a collection of folders, we have a "named library." Phrased as a "named library" is only relevant for our internal references in the use of the "library name" that a user *may* supply for commands.

## 2. Library Database

The "library database" is an interaction term for the VBR product. It has some crossover in concept to VDF, but not in name. VDF's GUI uses "Scan" and "Database" and allows access to only a single database at a time through it's instrumentation and interface. VBR is "inventing" the term "library" as an interaction term and it is a suitable term inasmuch as a user likely uses the term to refer to their media.

A "library database," is, conceptually in the user's mind, a nebulous collection of data and metadata that corresponds to the videos in their library. A user isn't likely to think of the database as "a library", but more as something that "relates to a library." A user may not initially know that a database is, or can be, stored in a single file on disk. However, VBR, as a product, can quickly educate a user that a "library database" can be stored in a single file and that this file's name and location is user-selectable. A user might infer from this that they could potentially have multiple databases, each named differently. But, a user may need to be educated to this fact by VBR. Once the user understand this, they may map the database concept in their mind to the various "libraries" they have in their mind from section 1 above. "Oh, I see, I can have a database for my TV shows and movies, and another database for all of my downloaded courses!"

Once the user's mental model is connected to the interaction term of "library" and "library database", and once they've understood that a database can be named and stored in a location, they may have begun to decide where they'd want to store the database files for each of their own conceptual "libraries." VBR is not opinionated about where a database may be stored, but it offers defaults. The default being, one database for the user, stored in a system-provided user folder, with a default name. But, VBR allows a user to put the library in any readable/writable folder of their choosing and give any name that they choose.

The relationship of a user to "library databases" is therefore, one-to-many and there is a mental correlation of "library database" to either a "library" in section 1, or potentially, only folders within their conceptual model of a "library."

## 3. Bumper Catalog

The concept of a "bumper" should not be new to a user, but the term may be. "Bumpers" in the strict sense go between content and advertisements. However, in the sense of VBR, we don't actually care. A "bumper" could be that. But, it could also be the advertisement itself. Or, it could be a full segment that re-occurs in videos within a "library", or within multiple "libraries" (e.g. a repeated humorous segment in sketch comedy). It does not matter what the strict definition of "bumper" is, what matters is that it has enough of an established definition in the community to get a user "part way there." Our full definition of a "bumper" is any segment of video that a) recurs more than once within a user's "libraries", and b) is unwanted. VBR's goal is to allow the user to identify this "bumper" and automatically remove it from all the videos that a user specifies (which may or may not be a whole "library" or multiple "libraries").

Removing a "bumper" from videos isn't a one-shot or ephemeral action. A user may find more videos later that have a bumper that was previously identified and removed from videos. So, to facilitate this, we allow users to collect "bumpers" that they've identified into a "catalog." Like a "database," we can place the catalog in the default location and in the default way, but VBR will also allow a user to specify the location and name of a catalog. After all, a user may conceptually think of distinct collections of bumpers and want them stored and referenced separately. For example, a user may have one "bumper catalog" dedicated to YouTube advertisements for sponsors that they once had a relationship with in the past, but no longer and they want to remove these ads from their creative works to repurpose them. This user would have a completely separate catalog of bumpers that correspond to studio idents at the beginning of produced videos. Beyond that, though, there may be some motivation for users to share bumper catalogs with each other. Thus, necessitating that a user may have multiple "bumper catalogs" by name and location.

The relationship of a user to "bumper catalogs" is therefore one-to-many. However, there is potential for many-to-many if VBR begins to support sharing in the future.

## Portability

### Cases

There are a variety of cases of implicit portability that naturally fall out of users and their own interactions with their systems and their media.

1. A "library database" and a "bumper catalog" file may be moved. It may be backed up, copied to the cloud, or put on a NAS. This is expected interaction between a user and files on their system.
2. A "library" might move, too. A user may migrate a library from an SMB shared folder to an S3-compatible block storage.
3. A "bumper catalog" might be shared between users in completely different environments and applied to varying "libraries" and utilized with different "library databases."
4. A "bumper" might be shared between users in completely different environments and applied to varying "libraries" and utilized with different "library databases." It may further be exported/imported from one "catalog" to another.
5. Individual media items may be added to a "library".
6. Individual media items may be moved to a new location in a "library".
7. Individual media items may be deleted from a "library".
8. Individual media items may be renamed in a "library".
9. A "bumper" may be orphaned from the source video from which it was originally identified.

### VBR Handling

VBR will want to handle all of the cases; at least in a policy or strategy sense if not outright accommodations for the cases.

1. VBR should accept the contents of a "library database" or "bumper catalog" that has been moved.
2. VBR may support a library that has moved. (Currently, this is not supported, but there is motivation to support it. Making a user rescan just because the absolute path changed is a bit too much.) This may be deferred past v1, but I reserve the right to change my mind on this.
3. Given #1, it seems likely that a "bumper catalog" can be shared and the contents are applicable. There's no concrete reason that bumpers identified on one machine cannot be equally useful on another machine, even one operated by a different user. VBR should support this.
4. Individual "bumpers" should be browsable, saveable, removable, renamable, exportable, and importable with the v1 of VBR.
5. VBR should allow for an "update" of a "library" that handles new content without needing to rescan the entire "library".
6. VBR should be able identify media in a "library" based on its contents, not its path, and re-map the file's location. This may be out-of-scope for v1.
7. VBR should be able to identify media in the "library database" that no longer exists on disk and remove it from the "database." This may necessitate a "clean" function. However, "clean" is currently the term used to clean up the temporary files left behind from a previous "remove" operation.
8. Conceptually different from #6 in a user's mind, but probabaly it is technically the same. VBR should support this, but may be out-of-scope for v1.
9. VBR needs to support orphaned bumpers in v1. In theory, once removed, all bumpers will become orphans, but their utility remains. The technical consequences of this are unresolved.

## Proposal

1. Reinforce the conceptual, user-oriented notion of a "library", A "library" is a collection of folders containing a user's media
	1. Support a user to specify a "library name" on commands
	2. Support "library paths" (plural) on commands (this is decidedly *not* the "library database" path)
	3. Support "library path exclusions" on commands
	4. Remain consistent from one CLI command to another
	5. Maintain consistency in the GUI
	6. Support "libraries" in both ad-hoc scenarios and in pre-scanned "database" scenarios on all commands (other than commands that explicitly establish a new "database")
2. Establish and reinforce the interaction term "library database" or "database" for short
	1. Give users clear documentation on what a "database" is, conceptually, and document that it is stored in a single file
	2. Support a "default" "database" for users not wanting the level of control afforded by multiple files
	3. Support users providing a single, named file for a "database".
	4. Correlate exactly one database file to one *named* "library" (see 1.1)
	5. Do not conflate "library name" with "database filename" unless the user themselves name the file the same as the "library name"
	6. Remain consistent from one CLI command to another
	7. Maintain consistency in the GUI
	8. Support using a "database" for any command or operation that may support a "library" (see 1) (with the exception of creating a new "database" file)
3. Establish and reinforce the interaction term "bumper"
	1. Identify, in documentation and in the GUI, how VBR's use of the term "bumper" is looser than cultural or industry norms
	2. Allow users to list, browse, and peruse "bumpers" as individual items on the CLI and in the GUI
	3. Support creating new "bumpers"
	4. Support deleting already established "bumpers"
	5. Support renaming already established "bumpers"
	6. Support editing details of already established "bumpers" (all details, precise length, description, tags, etc.) (Not discussed here, but there are use cases in which the identifying portion of a video is 5s, but the removable portion may be 10s.)
	7. Support duplicating an already established "bumper" to modify its details (see 3.6) for other uses.
	8. Remain consistent from one CLI command to another
	9. Maintain consistency in the GUI
	10. Support referencing a bumper in an ad-hoc scenario, or by referencing an already established "bumper" in CLI commands and GUI
4. Establish and reinforce the interaction term "bumper catalog" or "catalog"
	1. Give users clear documentation on what a "catalog" is, conceptually, and document that it is stored in a single file
	2. Support a "default" "catalog" for users not wanting the level of control afforded by multiple files
	3. Support users providing a single, named file for a "catalog"
	4. Do not explicitly correlate a "catalog" to either a "library" nor a "database"
	5. Remain consistent from one CLI command to another
	6. Maintain consistency in the GUI
	7. Support supplying a "catalog" file + "bumper" identification for commands that would require a "bumper" in both the CLI and the GUI
