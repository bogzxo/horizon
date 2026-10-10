<?xml version="1.0" encoding="UTF-8"?>
<tileset version="1.10" tiledversion="1.11.0" name="tiles" tilewidth="16" tileheight="16" tilecount="16" columns="4">
 <image source="tiles.png" width="64" height="64"/>
 <tile id="2">
  <animation>
   <frame tileid="2" duration="200"/>
   <frame tileid="3" duration="300"/>
  </animation>
 </tile>
 <tile id="4">
  <objectgroup draworder="index" id="2">
   <object id="1" x="0" y="8" width="16" height="8"/>
  </objectgroup>
 </tile>
 <tile id="5" type="sign">
  <properties>
   <property name="collidable" type="bool" value="true"/>
   <property name="label" value="from the tile"/>
  </properties>
 </tile>
</tileset>