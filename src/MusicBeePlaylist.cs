using System.Collections;
using System.Collections.Generic;
using static MusicBeePlugin.Plugin;

namespace DAAP {
    
    public interface IPlaylist
    {
        ContentNode ToTracksNode(int[] deletedIds);
        int[] GetDeletedIds(int revision);
    }

    public class MusicBeePlaylist : IPlaylist {
        // 1 is always base playlist
        private static int nextid = 2;
        
        private int id;
        private string playlistUrl;
        private int nextContainerId = 1;

        private struct PlaylistEntry
        {
            public int itemId;
            public int containerId;

            public ContentNode ToPlaylistNode()
            {
                return Track.CreatePlaylistNode(itemId, containerId);
            }
        }

        private List<PlaylistEntry> tracks = new List<PlaylistEntry>();
        
        internal int Id {
            get { return id; }
            set { id = value; }
        }
        
        public string Name {
            get { return mbApi.Playlist_GetName(playlistUrl); }
        }

        public string Url
        {
            get { return playlistUrl;  }
        }

        internal MusicBeePlaylist () {
            id = nextid++;
        }

        public MusicBeePlaylist(int id, string url)
        {
            this.id = id;
            this.playlistUrl = url;
        }

        public MusicBeePlaylist (string url) : this () {
            this.playlistUrl = url;
            Update();
        }
        
        private int[] GetPlaylistIdsFromMusicBee()
        {
            string[] files = { };
            mbApi.Playlist_QueryFilesEx(playlistUrl, ref files);
            return mbTracks.GetIds(files);
        }
        
        public List<int> Update()
        {
            int[] ids = GetPlaylistIdsFromMusicBee();
            List<int> removedIds = new List<int>();

            int idsIndex = 0, tracksIndex = 0;
            while (idsIndex < ids.Length && tracksIndex < tracks.Count) {
                if (ids[idsIndex] != tracks[tracksIndex].itemId) {
                    tracks.RemoveAt(tracksIndex);
                    removedIds.Add(ids[idsIndex]);
                } else {
                    tracksIndex++;
                }

                idsIndex++;
            }

            if (idsIndex > tracksIndex || tracksIndex == 0) {
                for (idsIndex = tracksIndex; idsIndex < ids.Length; idsIndex++) {
                    tracks.Add(new PlaylistEntry { itemId = ids[idsIndex], containerId = nextContainerId++ });
                }
            }

            return removedIds;
        }

        public ContentNode ToTracksNode (int[] deletedIds) {
            ArrayList trackNodes = new ArrayList ();

            foreach(PlaylistEntry entry in tracks) {
                trackNodes.Add(entry.ToPlaylistNode());
            }
            
            ArrayList deletedNodes = null;
            if (deletedIds.Length > 0) {
                deletedNodes = new ArrayList ();

                foreach (int id in deletedIds) {
                    deletedNodes.Add (new ContentNode ("dmap.itemid", id));
                }
            }

            ArrayList children = new ArrayList ();
            children.Add (new ContentNode ("dmap.status", 200));
            children.Add (new ContentNode ("dmap.updatetype", deletedNodes == null ? (byte) 0 : (byte) 1));
            children.Add (new ContentNode ("dmap.specifiedtotalcount", tracks.Count));
            children.Add (new ContentNode ("dmap.returnedcount", tracks.Count));
            children.Add (new ContentNode ("dmap.listing", trackNodes));

            if (deletedNodes != null)
                children.Add (new ContentNode ("dmap.deletedidlisting", deletedNodes));
            
            
            return new ContentNode ("daap.playlistsongs", children);
        }

        internal ContentNode ToNode (bool basePlaylist) {

            ArrayList nodes = new ArrayList ();

            nodes.Add (new ContentNode ("dmap.itemid", id));
            nodes.Add (new ContentNode ("dmap.persistentid", (long) id));
            nodes.Add (new ContentNode ("dmap.itemname", Name));
            nodes.Add (new ContentNode ("dmap.itemcount", tracks.Count));
            
            return new ContentNode ("dmap.listingitem", nodes);
        }

        public int[] GetDeletedIds(int revision)
        {
            return Update().ToArray();            
        }
    }

}
